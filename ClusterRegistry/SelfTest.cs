using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Buffers.Binary;
using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;
using static ClusterRegistry.DirectGateway;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>
    /// Times the lineage-collapse closure, old algorithm against new, on a synthetic lineage of the
    /// requested size. Not part of the self-test: it exists so the rewrite's cost claim can be stated
    /// as a measurement instead of an exponent, at a scale a functional test never reaches (w19 had
    /// 2253 lineage records after four hours; a self-test run produces about 40).
    /// </summary>
    /// <remarks>
    /// The synthetic shape is the one the real registry produces: a chain of splits and merges where
    /// each operation consumes the previous operation's outputs, so collapse has to propagate the
    /// whole length of the chain - the case the old do/while converged on slowest. Only the LAST
    /// operation's outputs get a save, so nothing collapses until the propagation reaches it.
    /// </remarks>
    internal static void BenchmarkCollapse(int records)
    {
        var inputs = new PartitionVersion[records][];
        var outputs = new PartitionVersion[records][];
        for (int i = 0; i < records; i++)
        {
            inputs[i] = [new PartitionVersion((ulong)(i + 1), 1)];
            outputs[i] = [new PartitionVersion((ulong)(i + 2), 1)];
        }
        var durable = new HashSet<PartitionVersion> { new((ulong)(records + 1), 1) };

        // The previous algorithm: rescan every record until a pass changes nothing, and for each
        // unsatisfied output rescan every record again looking for a collapsed consumer.
        var oldFlags = new bool[records];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool changed;
        do
        {
            changed = false;
            for (int i = 0; i < records; i++)
            {
                if (oldFlags[i]) continue;
                bool collapsed = outputs[i].All(output => durable.Contains(output)
                    || Enumerable.Range(0, records).Any(j => oldFlags[j] && inputs[j].Contains(output)));
                if (!collapsed) continue;
                oldFlags[i] = true;
                changed = true;
            }
        } while (changed);
        clock.Stop();
        TimeSpan before = clock.Elapsed;

        // The current algorithm: one pass, then a worklist driven by which records produce a version
        // that a just-collapsed record consumes.
        var newFlags = new bool[records];
        clock.Restart();
        var byOutput = new Dictionary<PartitionVersion, List<int>>();
        for (int i = 0; i < records; i++)
            foreach (PartitionVersion output in outputs[i])
            {
                if (!byOutput.TryGetValue(output, out List<int>? producers)) byOutput[output] = producers = [];
                producers.Add(i);
            }
        var consumed = new HashSet<PartitionVersion>();
        var pending = new Queue<int>(Enumerable.Range(0, records));
        while (pending.Count != 0)
        {
            int index = pending.Dequeue();
            if (newFlags[index]) continue;
            if (!outputs[index].All(output => durable.Contains(output) || consumed.Contains(output))) continue;
            newFlags[index] = true;
            foreach (PartitionVersion input in inputs[index])
                if (consumed.Add(input) && byOutput.TryGetValue(input, out List<int>? enabled))
                    foreach (int next in enabled) pending.Enqueue(next);
        }
        clock.Stop();
        TimeSpan coldCurrent = clock.Elapsed;

        if (!oldFlags.SequenceEqual(newFlags))
            throw new InvalidOperationException("benchmark disagreement: the two closures differ");

        // STEADY STATE, which is the honest per-call number: the registry persists the collapsed
        // flags, so a save registration on a warm gateway finds everything already collapsed except
        // whatever the newest operations added. Cold closure (above) is what a WAL replay or a burst
        // that collapses a long chain pays; this is what an ordinary tee save pays.
        var warmOld = (bool[])oldFlags.Clone();
        warmOld[^1] = false;
        var warmDurable = new HashSet<PartitionVersion>(durable);
        clock.Restart();
        do
        {
            changed = false;
            for (int i = 0; i < records; i++)
            {
                if (warmOld[i]) continue;
                bool collapsed = outputs[i].All(output => warmDurable.Contains(output)
                    || Enumerable.Range(0, records).Any(j => warmOld[j] && inputs[j].Contains(output)));
                if (!collapsed) continue;
                warmOld[i] = true;
                changed = true;
            }
        } while (changed);
        clock.Stop();
        TimeSpan warmBefore = clock.Elapsed;

        var warmNew = (bool[])newFlags.Clone();
        warmNew[^1] = false;
        clock.Restart();
        var warmConsumed = new HashSet<PartitionVersion>();
        for (int i = 0; i < records; i++)
            if (warmNew[i])
                foreach (PartitionVersion input in inputs[i]) warmConsumed.Add(input);
        var warmPending = new Queue<int>();
        for (int i = 0; i < records; i++) if (!warmNew[i]) warmPending.Enqueue(i);
        while (warmPending.Count != 0)
        {
            int index = warmPending.Dequeue();
            if (warmNew[index]) continue;
            if (!outputs[index].All(output => durable.Contains(output) || warmConsumed.Contains(output))) continue;
            warmNew[index] = true;
            foreach (PartitionVersion input in inputs[index])
                if (warmConsumed.Add(input) && byOutput.TryGetValue(input, out List<int>? enabled))
                    foreach (int next in enabled) warmPending.Enqueue(next);
        }
        clock.Stop();
        Console.WriteLine($"  steady state (all but one record already collapsed, i.e. an ordinary save "
            + $"registration): previous {warmBefore.TotalMilliseconds:F2} ms, current {clock.Elapsed.TotalMilliseconds:F2} ms");
        clock.Restart();
        clock.Stop();
        Console.WriteLine($"lineage collapse over {records} records, chained so collapse propagates the "
            + $"full length: previous {before.TotalMilliseconds:F1} ms, current {coldCurrent.TotalMilliseconds:F1} ms "
            + $"({(coldCurrent.TotalMilliseconds <= 0.05 ? ">1000" : (before.TotalMilliseconds / coldCurrent.TotalMilliseconds).ToString("F0"))}x), "
            + $"same {newFlags.Count(x => x)} records collapsed");
    }

    private const string CurrentToken = "Y3VycmVudC10ZXN0LXRva2VuLTAxMjM0NTY3ODkwMTIzNDU=";
    private const string PreviousToken = "cHJldmlvdXMtdGVzdC10b2tlbi0wMTIzNDU2Nzg5MDEyMzQ1";
    private static readonly NodeAdmission Admission = new(CurrentToken, null, "cluster", "world", "config", "binary", "types", "settings", 3000);

    /// The self-test is addressable by section so that each TestPlan scenario can run and cite the
    /// one it owns - `--self-test wal-recovery` is P1-GWY-02's evidence, not a line in a wall of
    /// output that also happened to pass.
    public static readonly (string Name, Action Body)[] Sections =
    [
        ("accounting-clock", TestAccountingClock),
        ("diagnostic-reports", TestDiagnosticReports),
        ("linux-limits", TestLinuxLimits),
        ("endpoints", TestEndpoints),
        ("option-environment", TestOptionEnvironment),
        ("network-security", TestNetworkSecurityOptions),
        ("secret-redaction", TestSecretRedaction),
        ("partition-placement", TestPartitionPlacement),
        ("cost-model-file", TestCostModelFile),
        ("codec", TestCodec),
        ("admin-contract", TestAdminContract),
        ("client-diagnostics", TestClientDiagnostics),
        ("offline-admin", TestOfflineAdmin),
        ("executor-contract", TestExecutorContract),
        ("executor-spawning-receipt", TestExecutorSpawningBeforeReceipt),
        ("managed-deployment", TestManagedDeployment),
        ("managed-hot-spares", TestManagedHotSpares),
        ("managed-hung-node-kill", TestManagedHungNodeKill),
        ("managed-rotation", TestManagedRotation),
        ("managed-removed-slots", TestManagedRemovedSlots),
        ("managed-configuration-drift", TestManagedConfigurationDrift),
        ("managed-restore", TestManagedRestore),
        ("managed-recovery", TestManagedRecovery),
        ("managed-recovery-update", TestManagedRecoveryUpdate),
        ("plugin-sdk-requirement", TestPluginSdkRequirement),
        ("admin-auth", TestAdminAuth),
        ("admin-events", TestAdminEvents),
        ("admin-operations", TestAdminOperations),
        ("admin-fleet", TestAdminFleet),
        ("admin-lifecycle", TestAdminLifecycle),
        ("plugin-services", TestPluginServices),
        ("global-route", TestGlobalRoute),
        ("plugin-persistence", TestPluginPersistence),
        ("admin-admission", TestAdminAdmission),
        ("player-moderation", TestPlayerModeration),
        ("admin-artifacts", TestAdminArtifacts),
        ("registry-copy", TestRegistryCopy),
        ("registry-backup-retention", TestRegistryBackupRetention),
        ("packet-queue", TestPacketQueue),
        ("multiplexing", TestMultiplexing),
        ("multiplexing-external", TestMultiplexingExternal),
        ("steam-external", TestSteamExternal),
        ("steam-reopen-without-login", TestSteamReopenWithoutLogin),
        ("steam-node-dead", TestSteamNodeDead),
        ("steam-deferred-route", TestSteamDeferredRoute),
        ("steam-home-recovery-hold", TestSteamHomeRecoveryHold),
        ("steam-admitted-while-paused", TestSteamAdmittedWhilePaused),
        ("joining-client-placement", TestJoiningClientPlacement),
        ("join-waits-for-moving-home", TestJoinWaitsForMovingHome),
        ("balance-test-trigger", TestBalanceTestTrigger),
        ("cost-model-overrides", TestCostModelOverrides),
        ("cost-model-override-race", TestCostModelOverrideRace),
        ("balance-hitch-price", TestBalanceHitchPrice),
        ("balance-min-gain", TestBalanceMinGain),
        ("balance-low-water", TestBalanceLowWater),
        ("balance-residence-size", TestBalanceResidenceSize),
        ("balance-target-budget", TestBalanceTargetBudget),
        ("balance-load-floor", TestBalanceLoadFloor),
        ("balance-peak-drop", TestBalancePeakDrop),
        ("balance-heavy-control", TestBalanceHeavyControl),
        ("balance-heavy-pin", TestBalanceHeavyPin),
        ("balance-heavy-split", TestBalanceHeavySplit),
        ("balance-heavy-release", TestBalanceHeavyRelease),
        ("balance-heavy-early", TestBalanceHeavyEarlyEvacuation),
        ("balance-heavy-merge", TestBalanceHeavyMerge),
        ("balance-heavy-drain", TestBalanceHeavyDrain),
        ("balance-heavy-spare", TestBalanceHeavySpare),
        ("control-bodyless-post", TestControlBodylessPost),
        ("balance-simulation-speed", TestBalanceSimulationSpeed),
        ("offline-wake-placement", TestOfflineWakePlacement),
        ("lookahead-max-speed", TestLookaheadMaxSpeed),
        ("relay-join-admission", TestRelayJoinAdmission),
        ("dead-client-session", TestDeadClientSession),
        ("attach-retry-switch-hold", TestAttachRetrySwitchHold),
        ("join-accept-stale-peer", TestJoinAcceptStalePeer),
        ("handover-capture", TestHandoverCapture),
        ("handover-telemetry", TestHandoverTelemetry),
        ("handover-config", TestHandoverConfig),
        ("spare-nodes", TestSpareNodes),
        ("spare-recovery", TestSpareRecovery),
        ("spare-recovery-spare-dies", TestSpareRecoverySpareDies),
        ("node-pair", TestNodePair),
        ("cost-model", TestCostModel),
        ("vanilla-join-codec", TestVanillaJoinCodec),
        ("admission", TestAdmission),
        ("node-admission", TestNodeAdmission),
        ("world-settings-hash", TestWorldSettingsHash),
        ("registry", TestRegistry),
        ("topology-replay-after-close", TestTopologyReplayAfterClose),
        ("async-apply-contract", TestAsyncApplyContract),
        ("balance-pin-lifecycle", TestBalancePinLifecycle),
        ("balance-waits-for-colocation", TestBalanceWaitsForColocation),
        ("automatic-pass-partial", TestAutomaticPassPartial),
        ("fewest-partitions-placement", TestFewestPartitionsPlacement),
        ("node-scaling", TestNodeScaling),
        ("node-control-ids", TestNodeControlIds),
        ("node-control-states", TestNodeControlStates),
        ("node-control-policy", TestNodeControlPolicy),
        ("node-control-remove-recycle", TestNodeControlRemoveRecycle),
        ("node-control-force", TestNodeControlForce),
        ("node-control-history", TestNodeControlHistory),
        ("node-control-hosts-file", TestNodeControlHostsFile),
        ("node-control-selectors", TestNodeControlSelectors),
        ("gone-holder-restart", TestGoneHolderRestart),
        ("gone-holder-release", TestGoneHolderRelease),
        ("gone-holder-release-off-lock", TestGoneHolderReleaseOffLock),
        ("gone-holder-archive-cache", TestGoneHolderArchiveCache),
        ("gone-holder-evacuation", TestGoneHolderEvacuation),
        ("gone-holder-dead", TestGoneHolderDead),
        ("late-saves", TestLateSaves),
        ("host-store-rekey", TestHostStoreRekey),
        ("host-store-previous-package", TestHostStorePreviousPackage),
        ("host-store-node-stop", TestHostStoreNodeStop),
        ("host-store-grant", TestHostStoreGrant),
        ("host-store-install", TestHostStoreInstall),
        ("host-store-deletes", TestHostStoreDeletes),
        ("host-store-globals", TestHostStoreGlobals),
        ("host-store-mode", TestHostStoreMode),
        ("host-store-corruption", TestHostStoreCorruption),
        ("host-store-fresh-start", TestHostStoreFreshStart),
        ("host-store-unmoved", TestHostStoreUnmoved),
        ("host-store-release", TestHostStoreRelease),
        ("host-store-adopt", TestHostStoreAdopt),
        ("host-store-adopt-during-claim", TestHostStoreAdoptDuringClaim),
        ("save-file-worker", TestSaveFileWorker),
        ("replica-budget", TestReplicaBudget),
        ("voxel-carry-policy", TestVoxelCarryPolicy),
        ("redeliver-policy", TestRedeliverPolicy),
        ("save-sequence-after-rollback", TestSaveSequenceAfterRollback),
        ("global-save-stale", TestGlobalSaveStale),
        ("sigterm-flush", TestSigtermFlush),
        ("replica-alarm", TestReplicaAlarm),
        ("diag-stopped-rows", TestDiagSkipsStoppedRows),
        ("handover-blocked", TestHandoverBlocked),
        ("provenance", TestProvenance),
        ("character-homes", TestCharacterHomes),
        ("character-home-writer-fence", TestCharacterHomeWriterFence),
        ("character-reap-permits", TestCharacterReapPermits),
        ("allocation-crash-durability", TestAllocationCrashDurability),
        ("network-id-cold-start-reclaim", TestNetworkIdColdStartReclaim),
        ("character-home-lineage", TestCharacterHomeFollowsLineage),
        ("restart-reconciliation", TestRestartReconciliationDecision),
        ("handover-adopt-ready-transport-failure", TestHandoverAdoptReadyTransportFailure),
        ("handover-balance-readiness-budget", TestHandoverBalanceReadinessBudget),
        ("handover-abort-readopts-on-source", TestHandoverAbortReadoptsOnSource),
        ("handover-pass-skips-closed-node-item", TestHandoverPassSkipsClosedNodeItem),
        ("closed-node-placeholder-drains", TestClosedNodePlaceholderDrains),
        ("closed-node-placeholder-lease-lapse", TestClosedNodePlaceholderLeaseLapse),
        ("drain-skips-lease-held-target", TestDrainSkipsLeaseHeldTarget),
        ("handover-claim-pending-retry", TestHandoverClaimPendingRetry),
        ("handover-owner-row-released-in-flight", TestHandoverOwnerRowReleasedInFlight),
        ("handover-reconcile-stale-source", TestHandoverReconcileStaleSource),
        ("handover-reconcile-reregistered", TestHandoverReconcileReregistered),
        ("handover-reconcile-held-lease", TestHandoverReconcileHeldLease),
        ("live-process-hold", TestLiveProcessHold),
        ("live-process-hold-recovery", TestLiveProcessHoldRecovery),
        ("live-process-hold-host-store", TestLiveProcessHoldHostStore),
        ("handover-reconcile-committed-pruned", TestHandoverReconcileCommittedPruned),
        ("handover-finalization-lease", TestHandoverFinalizationLease),
        ("store-recovery", TestStoreRecovery),
        ("store-growth", TestStoreGrowth),
        ("leases", TestLeases),
        ("world-authority", TestWorldAuthority),
        ("wa-recovery-holder", TestWorldAuthorityRecoveryHolder),
        ("global-journal", TestGlobalJournal),
        ("global-replica-reservation", TestGlobalReplicaReservation),
        ("global-holder-withdraw", TestGlobalHolderWithdraw),
        ("voxel-catalog", TestVoxelCatalog),
        ("world-authority-shutdown", TestWorldAuthorityShutdown),
        ("global-save-stands-without-authority", TestGlobalSaveStandsWithoutAuthority),
        ("simulation-stall", TestSimulationStall),
        ("retirement-index", TestRetirementIndex),
        ("recovery-pending", TestRecoveryPending),
        ("replica-work-draining", TestReplicaWorkDraining),
        ("replica-work-order", TestReplicaWorkOrder),
        ("replica-work-spread", TestReplicaWorkSpread),
        ("replica-queue", TestReplicaQueue),
        ("save-archive", TestSaveArchive),
        ("archive-retention", TestArchiveRetention),
        ("archive-retention-guards", TestArchiveRetentionGuards),
        ("archive-retention-first-sight", TestArchiveRetentionFirstSight),
        ("archive-retention-pace", TestArchiveRetentionPace),
        ("archive-retention-knob", TestArchiveRetentionKnob),
        ("archive-retention-node-held", TestArchiveRetentionNodeHeld),
        ("relay-ipc", TestRelayIpc),
        ("relay-ipc-stale-answer", TestRelayIpcStaleAnswer),
        ("relay-ipc-dispose-mid-send", TestRelayIpcDisposeMidSend),
        ("stranded-partition", TestStrandedPartition),
        ("failed-recovery-backoff", TestFailedRecoveryBackoff),
        ("invariant-audit", TestInvariantAudit),
        ("route-over-report", TestRouteOverReport),
        ("drain-sources", TestDrainSources),
        ("session-away-from-body", TestSessionsAwayFromBody),
        ("finish-switching", TestFinishSwitching),
        ("node-plan", TestNodePlan),
        ("cluster-shutdown", TestClusterShutdown),
        ("stranded-shutdown", TestStrandedShutdown),
        ("shutdown-unsaved-split-output", TestShutdownUnsavedSplitOutput),
        ("shutdown-memberless-unsaved", TestShutdownMemberlessUnsaved),
        ("save-response-isolation", TestSaveResponseIsolation),
        ("lineage-cover-walk", TestLineageCoverWalk),
        ("lineage-same-pass", TestLineageSamePass),
        ("split-keeps-input-id", TestSplitKeepsInputId),
        ("topology-transfer", TestTopologyTransfer),
        ("offline-partitions", TestOfflinePartitions),
        ("remote-use", TestRemoteUse),
        ("npc-census", TestNpcCensus),
        ("offline-grids", TestOfflineGrids),
        ("drain-offline", TestDrainOffline),
        ("recovery-homed-elsewhere", TestRecoveryHomedElsewhere),
        ("recovery-home-versions", TestRecoveryHomeVersions),
        ("partitions-report-filter", TestPartitionsReportFilter),
        ("reap-empty-partition", TestReapEmptyPartition),
        ("recovered-empty-release", TestRecoveredEmptyRelease),
        ("json-streaming", TestJsonStreaming),
        ("json-keepalive-latency", TestJsonKeepAliveLatency),
        ("http-nodelay-latency", TestHttpNoDelayLatency),
        ("multihost-node-addressing", TestMultiHostNodeAddressing),
        ("multihost-credential-rule", TestMultiHostCredentialRule),
        ("multihost-start-credentials", TestMultiHostStartCredentials),
        ("multihost-replica-hosts", TestMultiHostReplicaHosts),
        ("multihost-network-window", TestMultiHostNetworkWindow),
        ("multihost-window-hold-readers", TestMultiHostWindowHoldReaders),
        ("multihost-window-hold-begin", TestMultiHostWindowHoldBegin),
        ("multihost-window-recovery-holder", TestMultiHostWindowRecoveryHolder),
        ("multihost-transport-counters", TestMultiHostTransportCounters),
    ];

    public static void Run(string? section = null)
    {
        // The fixtures name their hosts ("polaris", "ai"); a legacy row without a host falls back to the machine name,
        // so on any other machine (CI) gone-holder-restart found no proxy. CLUSTER_SELFTEST_MACHINE_HOST reproduces that.
        GatewayRegistry.MachineHost = Environment.GetEnvironmentVariable("CLUSTER_SELFTEST_MACHINE_HOST") ?? "polaris";
        if (section != null && !Sections.Any(entry => string.Equals(entry.Name, section, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"unknown self-test section '{section}'; known: {string.Join(", ", Sections.Select(entry => entry.Name))}");
        // T-0347: no late-save hold in the fixtures - they reopen a registry and claim at once, with no node there to
        // report its host's journals as every real node does at registration. late-saves sets the hold it tests.
        GatewayRegistry.LateSaveHold = TimeSpan.Zero;
        // T-0227: every registry write in every section is checked against a reload of the store.
        GatewayRegistry.VerifyStoreAfterWrite = true;
        // SELFTEST_KEEP_GOING=1 runs every section and lists the failures, instead of stopping at the first.
        bool keepGoing = Environment.GetEnvironmentVariable("SELFTEST_KEEP_GOING") == "1";
        var failed = new List<string>();
        foreach ((string name, Action body) in Sections)
        {
            if (section != null && !string.Equals(name, section, StringComparison.OrdinalIgnoreCase)) continue;
            GatewayRegistry.StoreDivergence = null;
            GatewayRegistry.ReplicaDivergence = null;
            void RunSection()
            {
                body();
                if (GatewayRegistry.StoreDivergence is { } divergence)
                    throw new InvalidOperationException("store verifier: " + divergence.Message, divergence);
                if (GatewayRegistry.ReplicaDivergence is { } replica)
                    throw new InvalidOperationException("replica queue verifier: " + replica.Message, replica);
            }
            if (!keepGoing)
            {
                RunSection();
                Console.WriteLine($"self-test section {name} ok");
                continue;
            }
            try
            {
                RunSection();
                Console.WriteLine($"self-test section {name} ok");
            }
            catch (Exception error)
            {
                failed.Add(name);
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.WriteLine($"self-test section {name} FAILED: {error.GetType().Name}: {error.Message}");
                Console.WriteLine(error.StackTrace?.Split('\n').Take(6).Aggregate((a, b) => a + "\n" + b));
            }
        }
        if (failed.Count != 0) throw new InvalidOperationException("self-test sections failed: " + string.Join(", ", failed));
        Console.WriteLine("self-test ok");
    }

    private static void TestDiagnosticReports()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-diagnostics-" + Guid.NewGuid().ToString("N"));
        string? previousDirectory = Environment.GetEnvironmentVariable("ERROR_REPORTING_DIRECTORY");
        string? previousContext = Environment.GetEnvironmentVariable("ERROR_REPORTING_CONTEXT");
        try
        {
            Environment.SetEnvironmentVariable("ERROR_REPORTING_DIRECTORY", directory);
            Environment.SetEnvironmentVariable("ERROR_REPORTING_CONTEXT", "{\"runId\":\"test-run\"}");
            DiagnosticReports.Write("handover_failure", "Transfer failed", new IOException("capture error"), "transfer-1", 7, "import");
            using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Directory.GetFiles(directory, "*.json").Single()));
            Assert(report.RootElement.GetProperty("schemaVersion").GetInt32() == 1);
            Assert(report.RootElement.GetProperty("context").GetProperty("runId").GetString() == "test-run");
            Assert(report.RootElement.GetProperty("context").GetProperty("transferId").GetString() == "transfer-1");
            Assert(report.RootElement.GetProperty("context").GetProperty("partitionId").GetString() == "7");
            Assert(report.RootElement.GetProperty("exception").GetString()!.Contains("capture error"));
            Assert(Directory.GetFiles(directory, "*.tmp").Length == 0);
            // An unwritable destination must never alter the original failure path.
            Environment.SetEnvironmentVariable("ERROR_REPORTING_DIRECTORY", Directory.GetFiles(directory).Single());
            DiagnosticReports.Write("test", "unwritable spool");
            Environment.SetEnvironmentVariable("ERROR_REPORTING_DIRECTORY", directory);
            File.WriteAllText(Path.Combine(directory, "run.json"), "trusted run metadata");
            File.SetLastWriteTimeUtc(Path.Combine(directory, "run.json"), DateTime.UtcNow.AddYears(-1));
            while (Directory.GetFiles(directory, "*.json").Length < 257)
                File.WriteAllText(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), "{}");
            DiagnosticReports.Write("unhandled_exception", "fatal reserve");
            Assert(File.ReadAllText(Path.Combine(directory, "run.json")) == "trusted run metadata");
            Assert(Directory.GetFiles(directory, "*.json").Length == 257);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ERROR_REPORTING_DIRECTORY", previousDirectory);
            Environment.SetEnvironmentVariable("ERROR_REPORTING_CONTEXT", previousContext);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void TestEndpoints()
    {
        Assert(EndpointParser.Parse("127.0.0.1:27116", 27016).Equals(new IPEndPoint(IPAddress.Loopback, 27116)));
        Assert(EndpointParser.Parse("127.0.0.1", 27016).Equals(new IPEndPoint(IPAddress.Loopback, 27016)));
        Assert(!EndpointParser.TryParse("127.0.0.1:nope", 27016, out _));
        Assert(!EndpointParser.TryParse("127.0.0.1:99999", 27016, out _));
        Assert(IpNetwork.Parse("10.42.0.0/24").Contains(IPAddress.Parse("10.42.0.9")));
        Assert(!IpNetwork.Parse("10.42.0.0/24").Contains(IPAddress.Parse("10.42.1.9")));
        double gatewayMilliseconds = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
        Assert(double.IsFinite(gatewayMilliseconds) && gatewayMilliseconds >= 0);
    }

    /// <summary>
    /// The --cost-model FILE actually binds, field by field.
    ///
    /// Why this exists: CostModel is a positional record and the file is the ONLY way to change
    /// the T-0141 congestion weight without a rebuild. A JSON name that silently fails to bind
    /// would leave the weight at its 0 default, the deploy would be an unwitting no-op, and the
    /// experiment would read as "the fix does nothing" - indistinguishable from a real negative.
    /// --self-test returns before option parsing, so nothing else covers this path.
    /// </summary>
    private static void TestCostModelFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cost-model-selftest-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "LoadHomeWeight": 0.5, "BalanceRiskTolerance": 0.4 }""");
            CostModel loaded = CostModel.Load(path);
            Assert(loaded.LoadHomeWeight == 0.5);
            Assert(loaded.BalanceRiskTolerance == 0.4);
            // Unmentioned fields keep their defaults rather than zeroing - a partial file is the
            // normal case, since the point is to set one knob without restating the calibration.
            Assert(loaded.GammaPerSwitch == new CostModel().GammaPerSwitch);
            Assert(loaded.FrozenMsBase == new CostModel().FrozenMsBase);
            // A missing path is the default model, not a crash: the flag is optional.
            Assert(CostModel.Load(null).LoadHomeWeight == 0.0);
            Assert(CostModel.Load(path + ".absent").LoadHomeWeight == 0.0);
            // T-0393: no file runs the deployed pace knobs (the live A/B's winner), not the bare record defaults.
            Assert(CostModel.Load(null) == CostModel.Deployed() && CostModel.Load(null).HitchMsPerMove == 1000
                && CostModel.Load(null).BalanceLowWater == 0.15 && CostModel.Load(null).ResidenceMinFreezeMs == 0);
            // The shipped default must be inert. If this ever fails, a build has started changing
            // co-location behaviour without anyone setting a weight.
            Assert(new CostModel().LoadHomeWeight == 0.0);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void TestCodec()
    {
        var packet = new GatewayPacket(123, 2, GameFrame(9, 3), LiteNetLib.DeliveryMethod.ReliableOrdered);
        byte[] wire = DirectTransportCodec.EncodePacket(packet).CopyData();
        GatewayPacket decoded = DirectTransportCodec.DecodePacket(packet.ClientId, wire, packet.Delivery);
        Assert(decoded.ClientId == packet.ClientId);
        Assert(decoded.Channel == packet.Channel);
        Assert(decoded.Delivery == packet.Delivery);
        Assert(decoded.Payload.SequenceEqual(packet.Payload));
        Assert(DirectTransportCodec.DecodePacket(1, [0], DeliveryMethod.Unreliable) is { Channel: 0, Payload.Length: 0 });
        Assert(DirectTransportCodec.DecodePacket(1, [255, 1], DeliveryMethod.Unreliable) is { Channel: 255, Payload: [1] });
        AssertThrows(() => DirectTransportCodec.DecodePacket(1, [], DeliveryMethod.Unreliable));
        AssertThrows(() => DirectTransportCodec.EncodePacket(packet with { Channel = -1 }));
        AssertThrows(() => DirectTransportCodec.EncodePacket(packet with { Channel = 256 }));
        byte[] crcFrame = GameFrame(9, 7, crc: true);
        Assert(DirectTransportCodec.ParseGameFrame(crcFrame, DeliveryMethod.Unreliable) is { HasCrc: true, MessageId: 9 });
        crcFrame[^1] ^= 1;
        AssertThrows(() => DirectTransportCodec.ParseGameFrame(crcFrame, DeliveryMethod.Unreliable));
        Assert(DirectTransportCodec.ParseGameFrame([0xCE, 0, 0, 0, 0, 0, 1, 2], DeliveryMethod.ReliableOrdered).PartIndex == 1);
        AssertThrows(() => DirectTransportCodec.ParseGameFrame([0xCE, 0, 0, 0, 0, 0, 2, 2], DeliveryMethod.ReliableOrdered));

        var handshake = new NetDataReader(DirectTransportCodec.EncodeHandshake(packet.ClientId).CopyData());
        Assert(DirectTransportCodec.TryDecodeHandshake(handshake, out ulong handshakeId) && handshakeId == packet.ClientId && handshake.AvailableBytes == 0);
        var namedHandshake = new NetDataReader(DirectTransportCodec.EncodeHandshake(packet.ClientId, "  Agent ").CopyData());
        Assert(DirectTransportCodec.TryDecodeHandshake(namedHandshake, out ulong namedId, out string? assertedName)
            && namedId == packet.ClientId && assertedName == "Agent" && namedHandshake.AvailableBytes == 0);
        var longHandshake = new NetDataReader(DirectTransportCodec.EncodeHandshake(packet.ClientId, new string('n', 100)).CopyData());
        Assert(DirectTransportCodec.TryDecodeHandshake(longHandshake, out _, out string? longName) && longName == new string('n', 32));
        var blankHandshake = new NetDataReader(DirectTransportCodec.EncodeHandshake(packet.ClientId, "   ").CopyData());
        Assert(DirectTransportCodec.TryDecodeHandshake(blankHandshake, out _, out string? blankName) && blankName == null);
        var wrongVersion = new NetDataWriter();
        wrongVersion.Put("SE-DirectTransport-v2");
        wrongVersion.Put(packet.ClientId);
        Assert(!DirectTransportCodec.TryDecodeHandshake(new NetDataReader(wrongVersion.CopyData()), out _));
        var truncatedHandshake = new NetDataWriter();
        truncatedHandshake.Put(DirectTransportConstants.ProtocolKey);
        truncatedHandshake.Put((uint)packet.ClientId);
        Assert(!DirectTransportCodec.TryDecodeHandshake(new NetDataReader(truncatedHandshake.CopyData()), out _));

        var nodeHandshake = new NetDataReader(DirectTransportCodec.EncodeNodeLinkHandshake(CurrentToken).CopyData());
        Assert(DirectTransportCodec.TryDecodeHandshake(nodeHandshake, out ulong nodeId) && nodeId == DirectTransportCodec.NodeLinkId
            && DirectTransportCodec.TryDecodeNodeLinkHandshake(nodeHandshake, out string nodeToken) && nodeToken == CurrentToken);
        var wrongNodeVersion = new NetDataWriter();
        wrongNodeVersion.Put(DirectTransportConstants.ProtocolKey);
        wrongNodeVersion.Put(DirectTransportCodec.NodeLinkId);
        wrongNodeVersion.Put("SE-ClusterNodeLink-v1");
        wrongNodeVersion.Put(CurrentToken);
        var wrongNodeReader = new NetDataReader(wrongNodeVersion.CopyData());
        Assert(DirectTransportCodec.TryDecodeHandshake(wrongNodeReader, out _) && !DirectTransportCodec.TryDecodeNodeLinkHandshake(wrongNodeReader, out _));
        var untokenedNodeHandshake = new NetDataWriter();
        untokenedNodeHandshake.Put(DirectTransportConstants.ProtocolKey);
        untokenedNodeHandshake.Put(DirectTransportCodec.NodeLinkId);
        untokenedNodeHandshake.Put("SE-ClusterNodeLink-v2");
        var untokenedReader = new NetDataReader(untokenedNodeHandshake.CopyData());
        Assert(DirectTransportCodec.TryDecodeHandshake(untokenedReader, out _) && !DirectTransportCodec.TryDecodeNodeLinkHandshake(untokenedReader, out _));
        var trailingNodeHandshake = new NetDataWriter();
        trailingNodeHandshake.Put(DirectTransportCodec.EncodeNodeLinkHandshake(CurrentToken).CopyData());
        trailingNodeHandshake.Put((byte)0);
        var trailingNodeReader = new NetDataReader(trailingNodeHandshake.CopyData());
        Assert(DirectTransportCodec.TryDecodeHandshake(trailingNodeReader, out _) && !DirectTransportCodec.TryDecodeNodeLinkHandshake(trailingNodeReader, out _));

        var serverData = new GatewayPacket(123, 2, [0xCE, 0, 0, 0, 0, 0, 0, 1, 6, 0], LiteNetLib.DeliveryMethod.ReliableOrdered);
        Assert(DirectTransportCodec.TryGetGameMessageId(serverData, out byte messageId));
        Assert(messageId == 6);

        var splitPart = serverData with { Payload = [0xCE, 0, 0, 0, 0, 0, 1, 2] };
        Assert(!DirectTransportCodec.TryGetGameMessageId(splitPart, out _));

        NodeLinkPacket relay = new(NodeLinkMessage.Relay, 0x0102030405060708, 2, [3, 4], LiteNetLib.DeliveryMethod.ReliableOrdered);
        byte[] relayWire = DirectTransportCodec.EncodeNodeLink(relay).CopyData();
        Assert(relayWire.SequenceEqual(new byte[] { 1, 8, 7, 6, 5, 4, 3, 2, 1, 2, 3, 4 }));
        NodeLinkPacket relayDecoded = DirectTransportCodec.DecodeNodeLink(relayWire, relay.Delivery);
        Assert(relayDecoded.Kind == relay.Kind && relayDecoded.ClientId == relay.ClientId && relayDecoded.Channel == relay.Channel
            && relayDecoded.Payload.SequenceEqual(relay.Payload) && relayDecoded.Delivery == relay.Delivery);
        NodeLinkPacket attach = new(NodeLinkMessage.Attach, relay.ClientId, 0, [9, 8, 7], LiteNetLib.DeliveryMethod.ReliableOrdered);
        NodeLinkPacket attachDecoded = DirectTransportCodec.DecodeNodeLink(DirectTransportCodec.EncodeNodeLink(attach).CopyData(), attach.Delivery);
        Assert(attachDecoded.Kind == attach.Kind && attachDecoded.ClientId == attach.ClientId && attachDecoded.Payload.SequenceEqual(attach.Payload));
        NodeLinkPacket emptyRelay = DirectTransportCodec.DecodeNodeLink([1, 8, 7, 6, 5, 4, 3, 2, 1, 255], DeliveryMethod.Unreliable);
        Assert(emptyRelay.Channel == 255 && emptyRelay.Payload.Length == 0);
        NodeLinkPacket credit = DirectTransportCodec.DecodeNodeLink(DirectTransportCodec.EncodeNodeLink(
            new(NodeLinkMessage.Credit, 7, 0, [], DeliveryMethod.ReliableOrdered, 4096)).CopyData(), DeliveryMethod.ReliableOrdered);
        Assert(credit.Kind == NodeLinkMessage.Credit && credit.Credit == 4096);
        NodeLinkPacket global = DirectTransportCodec.DecodeNodeLink(DirectTransportCodec.EncodeNodeLink(
            new(NodeLinkMessage.Global, 0, 0, [1, 2, 3], DeliveryMethod.ReliableOrdered)).CopyData(), DeliveryMethod.ReliableOrdered);
        Assert(global.Kind == NodeLinkMessage.Global && global.ClientId == 0 && global.Payload.SequenceEqual(new byte[] { 1, 2, 3 }));
        // 2026-08-10 regression: a Global frame above 4 KB must decode - the node side never caps it,
        // and a 4096 cap here disconnect-looped every node link on the first full global-state sync.
        NodeLinkPacket bigGlobal = DirectTransportCodec.DecodeNodeLink(DirectTransportCodec.EncodeNodeLink(
            new(NodeLinkMessage.Global, 0, 0, new byte[65536], DeliveryMethod.ReliableOrdered)).CopyData(), DeliveryMethod.ReliableOrdered);
        Assert(bigGlobal.Kind == NodeLinkMessage.Global && bigGlobal.Payload.Length == 65536);
        AssertThrows(() => DirectTransportCodec.DecodeNodeLink([1, 0], LiteNetLib.DeliveryMethod.Unreliable));
        AssertThrows(() => DirectTransportCodec.EncodeNodeLink(attach with { Payload = new byte[4097] }));
        AssertThrows(() => DirectTransportCodec.DecodeNodeLink([255, 8, 7, 6, 5, 4, 3, 2, 1], DeliveryMethod.Unreliable));
    }

    private static void TestAdminContract()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-admin-contract-self-test-" + Guid.NewGuid());
        try
        {
            using var registry = new GatewayRegistry(directory, Admission);
            NodeRegistration node = Register(registry, "node-1", "127.0.0.1:28417", Guid.NewGuid());
            registry.PutNodeSlot("slot-1", "host-1", NodeRole.Regular, NodeDesiredState.Wanted);
            registry.ExecutorHeartbeat("executor-1",
                [new("slot-1", "attempt-1", SpawnState.Ready, "node-1", null)]);

            Admin.ClusterStatus status = registry.AdminStatus(connectedClients: 3);
            Assert(status.ClusterId == "cluster" && status.WorldId == "world"
                && status.Counts.ConnectedClients == 3 && status.Counts.Nodes == 1
                && status.Nodes.Single().SlotKey == "slot-1");
            Admin.NodePlan plan = registry.AdminNodePlan().Single();
            Assert(plan.SlotKey == "slot-1" && plan.Goal == Admin.NodeGoal.Wanted
                && plan.Observed == Admin.NodeObservation.Ready && plan.SpawnAllowed);

            PartitionOwnership owner = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
            Admin.RecoveryReadiness missingRecovery = registry.AdminRecoveryReadiness();
            Assert(missingRecovery.State == Admin.RecoveryReadinessState.NotReconstructible
                && missingRecovery.Missing.Single() == new Admin.MissingArtifact("partition", "1"));
            registry.RegisterSave(new SaveId(1, owner.Generation, 1), node.Node, node.Epoch,
                SaveKind.Periodic, 1, "checksum", 1, "node-1/save-1");
            registry.CreateOnlineSnapshot("online-1", 1);
            Admin.RecoveryReadiness recovery = registry.AdminRecoveryReadiness();
            Assert(recovery.State == Admin.RecoveryReadinessState.AtRisk
                && recovery.LatestPoint?.Consistency == Admin.RecoveryConsistency.CutConsistent
                && recovery.PartitionSaves is { Required: 1, Covered: 1, MinimumCopies: 1 }
                && recovery.Risks.Contains("partitionSingleCopy"));

            NodeRegistration replica = Register(registry, "node-2", "127.0.0.1:28418", Guid.NewGuid());
            registry.PutNodeSlot("slot-2", "host-2", NodeRole.Regular, NodeDesiredState.Wanted);
            registry.ExecutorHeartbeat("executor-2",
                [new("slot-2", "attempt-2", SpawnState.Ready, "node-2", null)]);
            registry.RegisterHolder(new SaveId(1, owner.Generation, 1), replica.Node, replica.Epoch,
                1, "checksum", "node-2/save-1", "http://127.0.0.1:29418");
            Admin.RecoveryReadiness ready = registry.AdminRecoveryReadiness();
            Assert(ready.State == Admin.RecoveryReadinessState.Ready
                && ready.PartitionSaves is { MinimumCopies: 2, MinimumDistinctHosts: 2 });

            var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new Admin.AdminEnvelope<Admin.ClusterStatus>(Admin.AdminProtocol.Version, DateTimeOffset.UtcNow, status), options);
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
            Assert(document.RootElement.GetProperty("protocolVersion").GetInt32() == 1
                && document.RootElement.GetProperty("data").GetProperty("phase").GetString() == "Serving");
            // T-0320: the node-control DTOs on the wire and in the operation list.
            TestNodeControlContract();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void TestAdminAuth()
    {
        string path = Path.Combine(Path.GetTempPath(), "cluster-gateway-admin-auth-self-test-" + Guid.NewGuid() + ".json");
        static string Hash(string token) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        try
        {
            File.WriteAllText(path, $$"""
                {
                  "tokens": [
                    { "name": "quasar", "scope": "manage", "sha256": "{{Hash("manage-token")}}", "previousSha256": "{{Hash("rotated-out")}}" },
                    { "name": "monitor", "scope": "query", "sha256": "{{Hash("query-token")}}" }
                  ]
                }
                """);
            var tokens = AdminTokenFile.Load(path);
            Assert(tokens.Match("manage-token") is { Name: "quasar", Scope: Admin.AdminScope.Manage });
            Assert(tokens.Match("rotated-out") is { Name: "quasar", Scope: Admin.AdminScope.Manage });
            Assert(tokens.Match("query-token") is { Name: "monitor", Scope: Admin.AdminScope.Query });
            Assert(tokens.Match("wrong-token") == null);

            // Rotation: rewriting the file revokes on the next request without a restart.
            File.WriteAllText(path, $$"""
                { "tokens": [ { "name": "monitor", "scope": "query", "sha256": "{{Hash("second-token")}}" } ] }
                """);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            Assert(tokens.Match("manage-token") == null);
            Assert(tokens.Match("second-token") is { Name: "monitor", Scope: Admin.AdminScope.Query });

            // A malformed edit keeps the last good set rather than failing open or closed.
            File.WriteAllText(path, "{ not json");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(4));
            Assert(tokens.Match("second-token") is { Name: "monitor" });

            // A malformed file fails the Gateway start eagerly.
            AssertArgument(() => AdminTokenFile.Load(path));
            AssertArgument(() => AdminTokenFile.Load(path + ".does-not-exist"));

            // The options layer refuses a dangling path before the listener ever starts.
            AssertArgument(() => GatewayOptions.Parse(["--steam"], name => name switch
            {
                "CLUSTER_ADMIN_TOKENS" => path + ".does-not-exist",
                _ => null,
            }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void TestAdminEvents()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-admin-events-self-test-" + Guid.NewGuid());
        try
        {
            // Append, cursor, and limit semantics.
            using (var log = new EventLog(directory))
            {
                var data = new Dictionary<string, string> { ["key"] = "value" };
                Assert(log.Append("first", "actor-1", "request-1", "target-1",
                    data) == 1);
                data["key"] = "changed after append";
                Assert(log.Append("second") == 2 && log.Append("third") == 3);
                (IReadOnlyList<EventLog.Entry> events, long next, long earliest) = log.Read(0, 2);
                Assert(events.Count == 2 && events[0].Type == "first" && events[1].Type == "second"
                    && next == 2 && earliest == 1);
                (events, next, _) = log.Read(next, 10);
                Assert(events.Count == 1 && events[0].Type == "third" && next == 3);
                (events, next, _) = log.Read(next, 10);
                Assert(events.Count == 0 && next == 3);
                Assert(events is not null && log.LastSeq == 3);
            }

            // The sequence and the tail survive a restart; old events read back from disk.
            using (var log = new EventLog(directory))
            {
                Assert(log.LastSeq == 3);
                Assert(log.Append("fourth") == 4);
                (IReadOnlyList<EventLog.Entry> events, _, _) = log.Read(0, 10);
                Assert(events.Count == 4 && events[0].Type == "first" && events[0].Data!["key"] == "value"
                    && events[3].Type == "fourth");
            }

            // Size-based roll bounds the disk tail. The live ring still reaches back to the
            // start; only a restart narrows the earliest retained sequence to what disk kept.
            string rollDirectory = directory + "-roll";
            using (var log = new EventLog(rollDirectory, rollBytes: 200))
            {
                for (int i = 0; i < 20; i++)
                    log.Append("filler-" + i);
                (IReadOnlyList<EventLog.Entry> events, _, long earliest) = log.Read(0, 1000);
                log.Flush();
                Assert(File.Exists(Path.Combine(rollDirectory, "events.jsonl.1")));
                Assert(earliest == 1 && events.Count == 20);
            }
            using (var log = new EventLog(rollDirectory))
            {
                Assert(log.LastSeq == 20);
                (IReadOnlyList<EventLog.Entry> events, _, long earliest) = log.Read(0, 1000);
                Assert(earliest > 1 && events.Count > 0 && events.Count < 20 && events[0].Seq == earliest);
            }
            Directory.Delete(rollDirectory, true);

            // Slow persistence cannot block event producers or recent-tail reads; shutdown retains ordering.
            string slowDirectory = directory + "-slow";
            using (var log = new EventLog(slowDirectory, capacity: 8))
            {
                var diskLock = typeof(EventLog).GetField("_diskLock",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(log)!;
                lock (diskLock)
                {
                    var producer = Task.Run(() =>
                    {
                        for (int i = 0; i < 100; i++) log.Append("queued-" + i);
                        var recent = log.Read(92, 8).Events;
                        Assert(recent.Count == 8 && recent[0].Seq == 93 && recent[^1].Seq == 100);
                    });
                    Assert(producer.Wait(TimeSpan.FromSeconds(2)));
                }
                log.Flush();
            }
            using (var restarted = new EventLog(slowDirectory))
            {
                Assert(restarted.LastSeq == 100 && restarted.Append("after-overload") == 101);
                Assert(restarted.Read(0, 1000).Events.Any(e => e.Type == "events.gap"));
            }
            Directory.Delete(slowDirectory, true);

            // A failed background write is observable at an explicit barrier and cannot fault shutdown.
            string failedDirectory = directory + "-failed";
            using (var log = new EventLog(failedDirectory))
            {
                var writer = (StreamWriter)typeof(EventLog).GetField("_writer",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(log)!;
                writer.Dispose();
                Assert(log.Append("still observable") == 1);
                bool failed = false;
                try { log.Flush(); } catch (ObjectDisposedException) { failed = true; }
                Assert(failed && log.Read(0, 10).Events.Count == 1);
            }
            Directory.Delete(failedDirectory, true);

            // Registry transitions land in the sink: phase changes, node lifecycle, shutdown.
            var emitted = new List<(string Type, string? Target)>();
            string registryDirectory = directory + "-registry";
            using (var registry = new GatewayRegistry(registryDirectory, Admission))
            {
                registry.EventSink = (type, target, _) => emitted.Add((type, target));
                NodeRegistration node = Register(registry, "node-1", "127.0.0.1:28417", Guid.NewGuid());
                Assert(emitted.Contains(("cluster.phase", null)) && emitted.Contains(("node.registered", "node-1")));
                // T-0207: A PARTITION ROW MUST NOT BE ABLE TO MOVE WITHOUT SAYING SO. Four occurrences
                // of the startup-claim race were diagnosed down to "what advanced this partition to
                // generation 1", and none could be answered: the event log carried node lifecycle only,
                // so every claim, release, lease expiry, handover and topology write was invisible. The
                // two asserts below are the ones that would have failed before that was fixed.
                registry.ClaimPartition(7UL, node.Node, node.Epoch, 0);
                Assert(emitted.Contains(("partition.claimed", "7")));
                registry.ReleasePartition(7UL, node.Node, node.Epoch, 1);
                Assert(emitted.Contains(("partition.released", "7")));
                registry.CloseNode(node.Node, node.Epoch);
                Assert(emitted.Contains(("node.closing", "node-1")));
                registry.BeginShutdown(emergency: false, TimeSpan.Zero);
                registry.CompleteShutdown(allowDirty: true);
                Assert(emitted.Count(x => x.Type == "cluster.phase") == 3);
            }
            Directory.Delete(registryDirectory, true);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void TestAdminOperations()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-admin-operations-self-test-" + Guid.NewGuid());
        try
        {
            string operationId;
            using (var registry = new GatewayRegistry(directory, Admission))
            {
                AdminOperationRecord begun = registry.BeginAdminOperation("shutdown", "grace=60", "quasar",
                    "key-1", "cluster", out bool created);
                Assert(created && begun.State == AdminOperationPhase.Running && begun.Actor == "quasar");
                operationId = begun.OperationId;

                // Same key + same request replays the running record instead of re-executing.
                AdminOperationRecord replayed = registry.BeginAdminOperation("shutdown", "grace=60", "quasar",
                    "key-1", "cluster", out created);
                Assert(!created && replayed.OperationId == operationId);

                // Same key bound to a different request is a conflict, not a silent second effect.
                AssertConflict(() => registry.BeginAdminOperation("shutdown", "grace=120", "quasar",
                    "key-1", "cluster", out _));
                AssertConflict(() => registry.BeginAdminOperation("save-all", "grace=60", "quasar",
                    "key-1", "cluster", out _));

                AdminOperationRecord completed = registry.CompleteAdminOperation(operationId,
                    new Dictionary<string, string> { ["phase"] = "Down" });
                Assert(completed.State == AdminOperationPhase.Succeeded && completed.Result!["phase"] == "Down");
                // Finishing twice keeps the first outcome.
                Assert(registry.FailAdminOperation(operationId, "late", "ignored").State == AdminOperationPhase.Succeeded);

                Admin.AdminOperation view = registry.AdminOperationView(operationId)!;
                Assert(view.State == Admin.AdminOperationState.Succeeded && view.IdempotencyKey == "key-1"
                    && view.Result!["phase"] == "Down" && view.Error == null);
                Assert(registry.AdminOperationView("missing") == null);

                AdminOperationRecord failed = registry.BeginAdminOperation("gateway-restart", "", "quasar",
                    "key-2", null, out _);
                registry.FailAdminOperation(failed.OperationId, "registry_unavailable", "boom");
                Admin.AdminOperation failedView = registry.AdminOperationView(failed.OperationId)!;
                Assert(failedView.State == Admin.AdminOperationState.Failed
                    && failedView.Error!.Code == "registry_unavailable");
                Assert(registry.AdminOperationList(10).Length == 2
                    && registry.AdminOperationList(1).Single().OperationId == failed.OperationId);
            }

            // The record replays across a registry restart: a lost response stays exactly-once.
            using (var recovered = new GatewayRegistry(directory, Admission))
            {
                AdminOperationRecord replayed = recovered.BeginAdminOperation("shutdown", "grace=60", "quasar",
                    "key-1", "cluster", out bool created);
                Assert(!created && replayed.OperationId == operationId
                    && replayed.State == AdminOperationPhase.Succeeded);
            }
            // T-0320: the node-control routes over HTTP (key, replay, reuse, 404 without an operation) and managed 409s.
            TestNodeControlAdminRoutes();
            TestNodeControlManaged();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void TestAdminFleet()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-admin-fleet-self-test-" + Guid.NewGuid());
        try
        {
            using var registry = new GatewayRegistry(directory, Admission);

            // Bootstrapping: not accepting players yet, warning with a stable reason code.
            Admin.ClusterStatus bootstrapping = registry.AdminStatus(0);
            Assert(!bootstrapping.AcceptingPlayers && bootstrapping.Health == Admin.AdminHealth.Warning
                && bootstrapping.ReasonCodes.Contains("bootstrapping"));

            NodeRegistration node = Register(registry, "node-1", "127.0.0.1:28417", Guid.NewGuid());
            registry.PutNodeSlot("slot-1", "host-1", NodeRole.Regular, NodeDesiredState.Wanted);
            registry.ExecutorHeartbeat("executor-1",
                [new("slot-1", "attempt-1", SpawnState.Ready, "node-1", null)]);

            // Serving and clean: accepting players, healthy, no reason codes.
            Admin.ClusterStatus serving = registry.AdminStatus(1);
            Assert(serving.AcceptingPlayers && serving.Health == Admin.AdminHealth.Healthy
                && serving.ReasonCodes.Length == 0 && serving.ObservedAt != default);

            // Paused: still Serving but not admitting; a warning the UI can name.
            Admin.ClusterStatus paused = registry.AdminStatus(1, paused: true);
            Assert(!paused.AcceptingPlayers && paused.Health == Admin.AdminHealth.Warning
                && paused.ReasonCodes.SequenceEqual(["paused"]));

            // Fleet view: slot identity, host, and lease freshness ride on each node.
            Admin.NodeStatus fleet = registry.AdminNodes().Single();
            Assert(fleet.Node == "node-1" && fleet.SlotKey == "slot-1" && fleet.Host == "host-1"
                && fleet.LeaseFresh && fleet.Role == Admin.NodeRole.Regular);

            Admin.WorldAuthorityStatus wa = registry.AdminWorldAuthority();
            Assert(wa.Node == null && wa.Generation == 0);

            PartitionOwnership owner = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
            Admin.PartitionSummary partition = registry.AdminPartitions().Single();
            Assert(partition.Partition == 1 && partition.Node == "node-1"
                && partition.Generation == owner.Generation && !partition.Quarantined && !partition.Retired);

            registry.RegisterSave(new SaveId(1, owner.Generation, 1), node.Node, node.Epoch,
                SaveKind.Periodic, 1, "checksum", 1, "node-1/save-1");
            registry.CreateOnlineSnapshot("online-1", 1);
            Admin.SnapshotSummary snapshot = registry.AdminSnapshots().Single();
            Assert(snapshot.SnapshotId == "online-1" && snapshot.Kind == Admin.AdminSnapshotKind.Online
                && snapshot.Partitions == 1 && snapshot.Dirty.Length == 0);

            // Draining then Down: reason codes track the phase.
            registry.BeginShutdown(emergency: false, TimeSpan.Zero);
            Assert(registry.AdminStatus(0).ReasonCodes.Contains("draining"));
            registry.CompleteShutdown(allowDirty: true);
            Admin.ClusterStatus down = registry.AdminStatus(0);
            Assert(!down.AcceptingPlayers && down.ReasonCodes.Contains("clusterDown"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void TestAdminLifecycle()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-admin-lifecycle-self-test-" + Guid.NewGuid());
        try
        {
            using var registry = new GatewayRegistry(directory, Admission);

            // Config view carries deployment identity and the policy revision.
            Admin.AdminConfig initial = registry.AdminConfig(TimeSpan.FromSeconds(10));
            Assert(initial.ClusterId == "cluster" && initial.ClusterConfigHash == "config"
                && initial.BinaryVersion == "binary" && initial.ReplicationTypeTableHash == "types"
                && initial.WorldSettingsHash == "settings" && initial.SyncDistance == 3000
                && initial.LeaseTtlSeconds == 10 && initial.PolicyRevision == 0 && initial.Slots.Length == 0);

            // CAS policy update: upsert slots, bump revision; a stale revision is refused.
            long revision = registry.AdminSetSlots(0,
            [
                new Admin.AdminSlotSpec("slot-1", "host-1", Admin.NodeRole.Regular, Admin.NodeGoal.Wanted),
                new Admin.AdminSlotSpec("wa-1", "host-1", Admin.NodeRole.WorldAuthority, Admin.NodeGoal.Wanted),
            ]);
            Assert(revision == 1);
            Admin.AdminConfig updated = registry.AdminConfig(TimeSpan.FromSeconds(10));
            Assert(updated.PolicyRevision == 1 && updated.Slots.Length == 2
                && updated.Slots.Single(x => x.SlotKey == "wa-1").Role == Admin.NodeRole.WorldAuthority);
            AssertConflict(() => registry.AdminSetSlots(0,
                [new Admin.AdminSlotSpec("slot-1", "host-1", Admin.NodeRole.Regular, Admin.NodeGoal.Draining)]));

            // Provenance-bound force removal: the exact observed incarnation, never a bare slot.
            NodeRegistration node = Register(registry, "node-1", "127.0.0.1:28417", Guid.NewGuid());
            registry.ExecutorHeartbeat("executor-1",
                [new("slot-1", "attempt-1", SpawnState.Ready, "node-1", null)]);
            AssertConflict(() => registry.AdminKillNode("slot-9", "node-1", node.Epoch));
            AssertConflict(() => registry.AdminKillNode("slot-1", "node-2", node.Epoch));
            AssertConflict(() => registry.AdminKillNode("slot-1", "node-1", node.Epoch + 1));
            NodeSlotPlan killed = registry.AdminKillNode("slot-1", "node-1", node.Epoch);
            Assert(killed.IncumbentAction == NodeIncumbentAction.Kill
                && killed.IncumbentNode == "node-1" && killed.IncumbentEpoch == node.Epoch);
            // The slot stays wanted: replacement spawns only after the executor reports Gone.
            NodeSlotPlan planned = registry.NodePlan().Single(x => x.SlotKey == "slot-1");
            Assert(planned.Desired == NodeDesiredState.Wanted && !planned.SpawnAllowed);
            registry.ExecutorHeartbeat("executor-1",
                [new("slot-1", "attempt-1", SpawnState.Gone, null, null)]);
            NodeSlotPlan replacement = registry.NodePlan().Single(x => x.SlotKey == "slot-1");
            Assert(replacement.IncumbentAction == NodeIncumbentAction.None
                && replacement.Observed == SpawnState.Gone && replacement.SpawnAllowed);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void TestAdminAdmission()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-admin-admission-self-test-" + Guid.NewGuid());
        try
        {
            // Admin bans survive a restart: the store rewrites atomically and reloads.
            BanStore store = BanStore.Open(directory)!;
            Assert(store.Load().Length == 0);
            var book = new AdmissionBook(new AdmissionSettings());
            book.Ban(76561198000000001, true);
            book.Ban(76561198000000002, true);
            store.Save(book.BannedUsers());
            Assert(BanStore.Open(directory)!.Load().SequenceEqual([76561198000000001UL, 76561198000000002UL]));

            var recovered = new AdmissionBook(new AdmissionSettings());
            foreach (ulong banned in store.Load())
                recovered.Ban(banned, true);
            Assert(recovered.IsBanned(76561198000000001) && recovered.IsBanned(76561198000000002));

            // Unban persists the shrunken set.
            recovered.Ban(76561198000000001, false);
            store.Save(recovered.BannedUsers());
            Assert(store.Load().SequenceEqual([76561198000000002UL]));

            // A corrupt store degrades to no admin bans instead of failing the start.
            File.WriteAllText(Path.Combine(directory, "admission-bans.json"), "{ not json");
            Assert(BanStore.Open(directory)!.Load().Length == 0);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void TestAdminArtifacts()
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-gateway-admin-artifacts-self-test-" + Guid.NewGuid());
        try
        {
            // Checksum manifest: per-file SHA-256 and a stable manifest hash over sorted entries.
            string world = Path.Combine(root, "world");
            Directory.CreateDirectory(Path.Combine(world, "sub"));
            File.WriteAllText(Path.Combine(world, "a.sbc"), "alpha");
            File.WriteAllText(Path.Combine(world, "sub", "b.sbs"), "beta");
            (Dictionary<string, string> checksums, long sizeBytes, string manifest) = DirectGateway.ChecksumTree(world);
            Assert(checksums.Count == 2 && sizeBytes == 9 && checksums.ContainsKey("a.sbc")
                && checksums.ContainsKey("sub/b.sbs") && manifest.Length == 64);
            (_, _, string manifestAgain) = DirectGateway.ChecksumTree(world);
            Assert(manifest == manifestAgain);

            // Store round-trip, persistence, and explicit release deleting only inside the root.
            string artifactDirectory = Path.Combine(root, "world-op1");
            Directory.CreateDirectory(artifactDirectory);
            File.WriteAllText(Path.Combine(artifactDirectory, "checkpoint.sbc"), "x");
            var store = new ArtifactStore(root);
            var descriptor = new Admin.ArtifactDescriptor("world-op1", "SpaceEngineersVanillaWorld", "snap-1",
                Admin.ExportConsistency.Quiescent, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1),
                1, "manifest", checksums, new Admin.ArtifactRetrieval(Admin.ArtifactRetrievalKind.SharedPath,
                    Path.GetFullPath(artifactDirectory)), []);
            store.Add(descriptor);
            Assert(new ArtifactStore(root).List().Single().ArtifactId == "world-op1");
            Assert(store.Get("world-op1") != null && store.Get("missing") == null);
            Assert(store.Release("world-op1") && !Directory.Exists(artifactDirectory));
            Assert(!store.Release("world-op1") && store.List().Length == 0);

            // Expired artifacts vanish lazily, body included.
            string expiredDirectory = Path.Combine(root, "world-op2");
            Directory.CreateDirectory(expiredDirectory);
            store.Add(descriptor with
            {
                ArtifactId = "world-op2",
                ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(-1),
                Retrieval = new Admin.ArtifactRetrieval(Admin.ArtifactRetrievalKind.SharedPath,
                    Path.GetFullPath(expiredDirectory)),
            });
            Assert(store.List().Length == 0 && !Directory.Exists(expiredDirectory));

            // A descriptor pointing outside the export root is never deleted from disk.
            string outside = Path.Combine(Path.GetTempPath(), "cluster-gateway-artifact-outside-" + Guid.NewGuid());
            Directory.CreateDirectory(outside);
            try
            {
                store.Add(descriptor with
                {
                    ArtifactId = "world-op3",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(-1),
                    Retrieval = new Admin.ArtifactRetrieval(Admin.ArtifactRetrievalKind.SharedPath,
                        Path.GetFullPath(outside)),
                });
                Assert(store.List().Length == 0 && Directory.Exists(outside));
            }
            finally
            {
                Directory.Delete(outside, true);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void TestRegistryCopy()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-registry-copy-self-test-" + Guid.NewGuid());
        string copy = Path.Combine(directory, "copy");
        try
        {
            using var registry = new GatewayRegistry(Path.Combine(directory, "live"), Admission);
            NodeRegistration node = Register(registry, "node-1", "127.0.0.1:28417", Guid.NewGuid());
            PartitionOwnership owner = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
            registry.RegisterSave(new SaveId(1, owner.Generation, 1), node.Node, node.Epoch,
                SaveKind.Periodic, 1, "checksum", 1, "node-1/save-1");
            // Not yet flushed when the copy starts: the copy writes pending mutations out first.
            registry.CreateOnlineSnapshot("before-copy", 1);

            // A writer keeps committing while the copy reads; the copy is one consistent transaction.
            using var stop = new CancellationTokenSource();
            Task writer = Task.Run(() =>
            {
                for (int sequence = 2; !stop.IsCancellationRequested; sequence++)
                {
                    registry.RegisterSave(new SaveId(1, owner.Generation, sequence), node.Node, node.Epoch,
                        SaveKind.Periodic, sequence, "checksum", 1, "node-1/save-" + sequence);
                    registry.Flush();
                }
            });
            Directory.CreateDirectory(copy);
            registry.CopyStore(Path.Combine(copy, RegistryStore.FileName));
            stop.Cancel();
            writer.Wait();

            Assert(!File.Exists(Path.Combine(copy, RegistryStore.FileName + "-wal")));
            using var copied = new GatewayRegistry(copy, Admission);
            Assert(copied.AdminSnapshots().Any(x => x.SnapshotId == "before-copy"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// #1275 (F32): retention must not delete a save an outstanding registry backup catalogues before the backup has
    /// copied the node data. Three newer saves after the copy would otherwise let retention delete the copy's whole
    /// cover of the partition, and the restored world loses it.
    /// </summary>
    private static void TestRegistryBackupRetention()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-registry-backup-retention-self-test-" + Guid.NewGuid());
        string copy = Path.Combine(directory, "copy");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("registry-backup-retention: " + what); }
        try
        {
            using var registry = new GatewayRegistry(Path.Combine(directory, "live"), Admission, () => now, TimeSpan.FromDays(1));
            NodeRegistration node = Register(registry, "node-1", "127.0.0.1:28417", Guid.NewGuid());
            PartitionOwnership owner = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
            void Save(int sequence) => registry.RegisterSave(new SaveId(1, owner.Generation, sequence), node.Node, node.Epoch,
                SaveKind.Periodic, sequence, "checksum", 1, "node-1/save-" + sequence);
            for (int sequence = 1; sequence <= 3; sequence++) Save(sequence);
            Directory.CreateDirectory(copy);
            registry.CopyStore(Path.Combine(copy, RegistryStore.FileName), "registry-backup-1", now + TimeSpan.FromHours(6));
            for (int sequence = 4; sequence <= 6; sequence++) Save(sequence);
            for (int pass = 0; pass < 3; pass++) registry.PlanRetention();

            string[] Deleting() => registry.PendingDeletes(node.Node, node.Epoch).Select(x => x.Location).ToArray();
            string[] live;
            using (var copied = new GatewayRegistry(copy, Admission))
                live = copied.Saves().Where(x => !x.PendingDelete).SelectMany(x => x.Locations).ToArray();
            Check(live.Order().SequenceEqual(["node-1/save-1", "node-1/save-2", "node-1/save-3"]), "the copy catalogues the three saves before it");
            Check(!Deleting().Intersect(live).Any(), "retention deletes a save the outstanding backup catalogues");

            // Released: retention catches up and deletes the three oldest.
            Check(registry.ReleaseBackupHold("registry-backup-1") && !registry.ReleaseBackupHold("registry-backup-1"), "release ends the hold once");
            registry.PlanRetention();
            Check(Deleting().Order().SequenceEqual(live), "a released hold still keeps saves");

            // Expired: a hold the backup never released ends at its TTL.
            foreach (DeleteDirective deletion in registry.PendingDeletes(node.Node, node.Epoch))
                registry.AcknowledgeDelete(deletion.SaveId, deletion.Location);
            Check(Deleting().Length == 0, "acknowledged deletes are done");
            registry.CopyStore(Path.Combine(directory, "copy-2.db"), "registry-backup-2", now + TimeSpan.FromHours(6));
            for (int sequence = 7; sequence <= 9; sequence++) Save(sequence);
            registry.PlanRetention();
            Check(Deleting().Length == 0, "an outstanding hold lets retention delete");
            now += TimeSpan.FromHours(7);
            registry.PlanRetention();
            Check(Deleting().Order().SequenceEqual(["node-1/save-4", "node-1/save-5", "node-1/save-6"]), "an expired hold still keeps saves");
            Check(!registry.ReleaseBackupHold("registry-backup-2"), "the expired hold is gone");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void TestPacketQueue()
    {
        var bounded = new DirectGateway.BoundedPacketQueue(5, TimeSpan.MaxValue);
        Assert(bounded.TryEnqueue(new GatewayPacket(1, 2, [1, 2], DeliveryMethod.ReliableOrdered)));
        Assert(bounded.Count == 1 && bounded.Bytes == 4);
        Assert(!bounded.TryEnqueue(new GatewayPacket(1, 2, [3, 4], DeliveryMethod.ReliableOrdered)));
        Assert(bounded.TryDequeue(out GatewayPacket? queued) && queued!.Payload.SequenceEqual(new byte[] { 1, 2 }));
        Assert(bounded.Count == 0 && bounded.Bytes == 0);
        var expiring = new DirectGateway.BoundedPacketQueue(100, TimeSpan.Zero);
        Assert(expiring.TryEnqueue(new GatewayPacket(1, 2, [1], DeliveryMethod.ReliableOrdered)));
        Thread.Sleep(1);
        Assert(expiring.IsExpired(Stopwatch.GetTimestamp()));
    }

    private static void TestHandoverCapture()
    {
        static byte[] Capture(params (ulong Client, string State)[] clients)
        {
            using MemoryStream stream = new();
            using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(-1);
                writer.Write(2);
                writer.Write(clients.Length);
                foreach ((ulong client, string state) in clients)
                {
                    writer.Write(client);
                    writer.Write(System.Text.Encoding.UTF8.GetByteCount(state));
                }
                writer.Write(0);
                writer.Write(2);
                foreach ((ulong _, string state) in clients)
                    writer.Write(System.Text.Encoding.UTF8.GetBytes(state));
                writer.Write(new byte[] { 4, 5 });
            }
            return stream.ToArray();
        }

        HandoverCapture capture = HandoverCapture.Parse(Capture((7, "1,2")));
        Assert(capture.StateFor(7) == "1,2");
        Assert(capture.NetworkState == string.Empty);
        Assert(capture.Sector.SequenceEqual(new byte[] { 4, 5 }));

        // A multi-client capture keeps each client's view separate and addressable by id: the
        // whole point of the format-2 change is that one freeze serves every rider of a partition.
        HandoverCapture crew = HandoverCapture.Parse(Capture((7, "a"), (9, "bb"), (11, "")));
        Assert(crew.ClientStates.Count == 3);
        Assert(crew.StateFor(7) == "a" && crew.StateFor(9) == "bb" && crew.StateFor(11) == string.Empty);
        Assert(crew.ClientStateBytes == 3);
        Assert(crew.Sector.SequenceEqual(new byte[] { 4, 5 }));
        AssertThrows(() => crew.StateFor(13));

        // A clientless capture carries no views at all.
        Assert(HandoverCapture.Parse(Capture()).ClientStates.Count == 0);

        // A format-1 payload must be rejected outright, not mis-sliced: its first word is a
        // non-negative client-state length where format 2 writes the -1 marker.
        AssertThrows(() => HandoverCapture.Parse([3, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 49, 44, 50, 4, 5]));
        AssertThrows(() => HandoverCapture.Parse([255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    /// The fenced transfer's node-pair resolution: any distinct active regular pair, either
    /// direction - the symmetry P3-CLU-01/02 stand on. A closing source is legal (draining),
    /// a non-active target, a WA, or a backend mismatch is not.
    private static void TestNodePair()
    {
        static NodeRegistration Node(string name, string backend, string control,
            NodeState state = NodeState.Active, NodeRole role = NodeRole.Regular) =>
            new(name, 1, backend, DateTimeOffset.UtcNow, state, Role: role, ControlEndpoint: control);
        NodeRegistration[] nodes =
        [
            Node("node-1", "127.0.0.1:28417", "http://127.0.0.1:29417"),
            Node("node-2", "127.0.0.1:28418", "http://127.0.0.1:29418"),
            Node("node-3", "127.0.0.1:28419", "http://127.0.0.1:29419"),
            Node("wa-1", "127.0.0.1:28420", "http://127.0.0.1:29420", role: NodeRole.WA),
        ];
        static IPEndPoint Backend(int port) => new(IPAddress.Loopback, port);

        var forward = DirectGateway.ResolveNodePair(nodes, "127.0.0.1:29417", "127.0.0.1:29418", Backend(28418));
        Assert(forward is { Source.Node: "node-1", Target.Node: "node-2" });
        // The reverse direction resolves the same way - node-2 can give a player back.
        var reverse = DirectGateway.ResolveNodePair(nodes, "127.0.0.1:29418", "127.0.0.1:29417", Backend(28417));
        Assert(reverse is { Source.Node: "node-2", Target.Node: "node-1" });
        // Any third-node pair, and scheme-carrying endpoint arguments, resolve too.
        Assert(DirectGateway.ResolveNodePair(nodes, "http://127.0.0.1:29417/", "127.0.0.1:29419", Backend(28419))
            is { Source.Node: "node-1", Target.Node: "node-3" });
        // Backend must match the target registration.
        Assert(DirectGateway.ResolveNodePair(nodes, "127.0.0.1:29417", "127.0.0.1:29418", Backend(28419)) == null);
        // The WA never serves partitions.
        Assert(DirectGateway.ResolveNodePair(nodes, "127.0.0.1:29420", "127.0.0.1:29418", Backend(28418)) == null);
        Assert(DirectGateway.ResolveNodePair(nodes, "127.0.0.1:29417", "127.0.0.1:29420", Backend(28420)) == null);
        // A closing source still drains; a closed target accepts nothing.
        nodes[0].State = NodeState.Closed;
        Assert(DirectGateway.ResolveNodePair(nodes, "127.0.0.1:29417", "127.0.0.1:29418", Backend(28418))
            is { Source.Node: "node-1" });
        Assert(DirectGateway.ResolveNodePair(nodes, "127.0.0.1:29418", "127.0.0.1:29417", Backend(28417)) == null);
        // Distinct nodes required.
        Assert(DirectGateway.ResolveNodePair(nodes, "127.0.0.1:29418", "127.0.0.1:29418", Backend(28418)) == null);
    }

    // HotSpareNodes.md: a spare can be designated before its process registers, the flag rides the
    // node id across re-registration, a spare claims no recoveries and cannot be a handover target,
    // activation clears the flag, and the designation survives a gateway restart through the WAL.
    private static void TestSpareNodes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-spare-nodes-self-test-" + Guid.NewGuid());
        try
        {
            using (var registry = new GatewayRegistry(directory, Admission))
            {
                var emitted = new List<string>();
                registry.EventSink = (type, _, _) => emitted.Add(type);
                Assert(registry.SetNodeSpare("node-2", true) == null);
                NodeRegistration one = registry.RegisterNode(Join("node-1", "one", Guid.NewGuid()));
                NodeRegistration two = registry.RegisterNode(Join("node-2", "two", Guid.NewGuid()));
                Assert(!one.Spare && two.Spare);
                // Serving-age stamp: a normal node serves from registration, a spare only
                // from activation - so its idle time never counts against the lifetime.
                Assert(one.Activated == one.Registered && two.Activated == null);
                Assert(registry.ClaimRecoveries("node-2", two.Epoch, []).Length == 0);
                try { registry.Begin("h-1", 7, "node-1", one.Epoch, 0, "node-2", two.Epoch); Assert(false); }
                catch (RegistryConflictException error) { Assert(error.Message.Contains("hot spare")); }
                // Re-registration (crash + resurrection) keeps the designation.
                NodeRegistration reborn = registry.RegisterNode(Join("node-2", "two", Guid.NewGuid()));
                Assert(reborn.Spare && reborn.Epoch == two.Epoch + 1);
                NodeRegistration activated = registry.SetNodeSpare("node-2", false)!;
                Assert(!activated.Spare);
                Assert(activated.Activated != null && activated.Activated >= activated.Registered);
                Assert(emitted.Count(x => x == "node.spare") == 2);
                Assert(registry.SetNodeSpare("node-3", true) == null);
            }
            using (var reopened = new GatewayRegistry(directory, Admission))
            {
                Assert(!reopened.Nodes().Single(x => x.Node == "node-2").Spare);
                Assert(reopened.RegisterNode(Join("node-3", "three", Guid.NewGuid())).Spare);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    // T-0246: when a loaded node dies, the hot spare is promoted and recovers ALL of its partitions - in
    // one claim, with no survivor able to claim them first. T-0320: the dead id is NOT re-designated as the
    // spare (ids are never reused; node control refills the spare with a new id). The reservation ends when
    // the window runs out or the spare itself dies, and with no live spare a death recovers onto the
    // survivors as before.
    private static void TestSpareRecovery()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-spare-recovery-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            Assert(registry.SetNodeSpare("node-3", true) == null);
            NodeRegistration one = registry.RegisterNode(Join("node-1", "one", Guid.NewGuid()));
            NodeRegistration two = registry.RegisterNode(Join("node-2", "two", Guid.NewGuid()));
            NodeRegistration three = registry.RegisterNode(Join("node-3", "three", Guid.NewGuid()));
            Assert(three.Spare);
            void Own(NodeRegistration node, ulong partition)
            {
                PartitionOwnership owned = registry.ClaimPartition(partition, node.Node, node.Epoch, 0);
                registry.RegisterSave(new SaveId(partition, owned.Generation, 1), node.Node, node.Epoch, SaveKind.Periodic, 1,
                    "c" + partition, 1, node.Node + "/" + partition, entities: 3);
            }
            Own(one, 1);
            Own(one, 2);
            Own(one, 3);
            Own(two, 10);
            // node-1 stops renewing; node-2 and the spare keep heartbeating.
            now += TimeSpan.FromSeconds(6);
            registry.Heartbeat(two.Node, two.Epoch, [new(10, 1)]);
            registry.Heartbeat(three.Node, three.Epoch, []);
            now += TimeSpan.FromSeconds(5);
            Assert(registry.ExpireLeases() == 3);
            NodeRegistration[] rows = registry.Nodes();
            NodeRegistration promoted = rows.Single(x => x.Node == "node-3");
            Assert(!promoted.Spare && promoted.Activated == now && promoted.State == NodeState.Active);
            NodeRegistration deadSlot = rows.Single(x => x.Node == "node-1");
            Assert(deadSlot.State == NodeState.Dead && !deadSlot.Spare);
            // A survivor gets none of them, by auto-pick or by naming one, and is not answered with a conflict.
            Assert(registry.ClaimRecoveries(two.Node, two.Epoch, []).Length == 0);
            Assert(registry.ClaimRecoveries(two.Node, two.Epoch, [1, 2]).Length == 0);
            // The promoted spare gets all three in one claim, headroom notwithstanding.
            RecoveryClaim[] recovered = registry.ClaimRecoveries(three.Node, three.Epoch, []);
            Assert(recovered.Select(x => x.Ownership.Partition).Order().SequenceEqual([1UL, 2UL, 3UL]));
            Assert(recovered.All(x => x.Ownership.Node == "node-3" && x.ResolveCover.Length != 0));
            Assert(registry.ClaimRecoveries(two.Node, two.Epoch, []).Length == 0);
            // The dead id does not come back as the spare: a NEW id is the spare from here on.
            Assert(registry.SetNodeSpare("node-4", true) == null);
            NodeRegistration reborn = registry.RegisterNode(Join("node-4", "four", Guid.NewGuid()));
            Assert(reborn.Spare && reborn.Activated == null);
            Assert(registry.ClaimRecoveries(reborn.Node, reborn.Epoch, []).Length == 0);

            // The window: node-2 dies, node-4 (the new spare) is promoted but never claims. node-3 cannot
            // take node-2's partition inside the window, and can once it has run out.
            registry.Heartbeat(three.Node, three.Epoch, [new(1, 2), new(2, 2), new(3, 2)]);
            now += TimeSpan.FromSeconds(6);
            registry.Heartbeat(three.Node, three.Epoch, [new(1, 2), new(2, 2), new(3, 2)]);
            registry.Heartbeat(reborn.Node, reborn.Epoch, []);
            now += TimeSpan.FromSeconds(5);
            Assert(registry.ExpireLeases() == 1 && registry.GetPartitionOwner(10).Node == null);
            Assert(!registry.Nodes().Single(x => x.Node == "node-4").Spare && !registry.Nodes().Single(x => x.Node == "node-2").Spare);
            for (int second = 0; second < 29; second += 4)
            {
                registry.Heartbeat(three.Node, three.Epoch, [new(1, 2), new(2, 2), new(3, 2)]);
                registry.Heartbeat(reborn.Node, reborn.Epoch, []);
                registry.ExpireLeases();
                Assert(registry.ClaimRecoveries(three.Node, three.Epoch, []).Length == 0);
                now += TimeSpan.FromSeconds(4);
            }
            registry.Heartbeat(three.Node, three.Epoch, [new(1, 2), new(2, 2), new(3, 2)]);
            registry.Heartbeat(reborn.Node, reborn.Epoch, []);
            registry.ExpireLeases();
            // T-0337: the reservation is over and the pick is free, so node-3 (3 partitions) first waits for the empty
            // node-4 of its host to ask; node-4 never does, and RecoveryClaimPatience later node-3 takes it.
            Assert(registry.ClaimRecoveries(three.Node, three.Epoch, []).Length == 0);
            now += GatewayRegistry.RecoveryClaimPatience;
            registry.Heartbeat(three.Node, three.Epoch, [new(1, 2), new(2, 2), new(3, 2)]);
            registry.Heartbeat(reborn.Node, reborn.Epoch, []);
            Assert(registry.ClaimRecoveries(three.Node, three.Epoch, []).Single().Ownership.Partition == 10);

            // No live spare left (none designated): node-3 dies and node-4 recovers its
            // partitions the pre-T-0246 way, one per claim, with nothing reserved.
            registry.Heartbeat(reborn.Node, reborn.Epoch, []);
            now += TimeSpan.FromSeconds(6);
            registry.Heartbeat(reborn.Node, reborn.Epoch, []);
            now += TimeSpan.FromSeconds(5);
            Assert(registry.ExpireLeases() == 4);
            Assert(registry.Nodes().Single(x => x.Node == "node-3") is { State: NodeState.Dead, Spare: false });
            Assert(registry.ClaimRecoveries(reborn.Node, reborn.Epoch, []).Length == 1);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    // T-0246: a promoted spare that dies before claiming releases the reservation at its Dead verdict,
    // not at the end of the window.
    private static void TestSpareRecoverySpareDies()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-spare-recovery-dies-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            var dead = new List<(string? Node, string? Endpoint)>();
            registry.EventSink = (type, target, data) =>
            {
                if (type == "node.dead") dead.Add((target, data != null && data.TryGetValue("endpoint", out string? at) ? at : null));
            };
            Assert(registry.SetNodeSpare("node-3", true) == null);
            NodeRegistration one = registry.RegisterNode(Join("node-1", "one", Guid.NewGuid()));
            NodeRegistration two = registry.RegisterNode(Join("node-2", "two", Guid.NewGuid()));
            NodeRegistration three = registry.RegisterNode(Join("node-3", "three", Guid.NewGuid()));
            PartitionOwnership owned = registry.ClaimPartition(1, one.Node, one.Epoch, 0);
            registry.RegisterSave(new SaveId(1, owned.Generation, 1), one.Node, one.Epoch, SaveKind.Periodic, 1, "c1", 1, "one/1", entities: 3);
            // node-1 dies at 10 s while node-2 and the spare renew at 6 s (leases to 16 s).
            now += TimeSpan.FromSeconds(6);
            registry.Heartbeat(two.Node, two.Epoch, []);
            registry.Heartbeat(three.Node, three.Epoch, []);
            now += TimeSpan.FromSeconds(5);
            Assert(registry.ExpireLeases() == 1 && !registry.Nodes().Single(x => x.Node == "node-3").Spare);
            // T-0244: the Dead event names the node's endpoint, which the gateway moves clients off.
            Assert(dead.Count == 1 && dead[0].Node == "node-1" && dead[0].Endpoint == one.Endpoint);
            Assert(registry.ClaimRecoveries(two.Node, two.Epoch, []).Length == 0);
            // The promoted spare never renews again; node-2 does. At 17 s the spare is Dead, 24 s before
            // the window would have ended, and node-2 recovers the partition at once.
            registry.Heartbeat(two.Node, two.Epoch, []);
            now += TimeSpan.FromSeconds(6);
            Assert(registry.ExpireLeases() == 0 && registry.Nodes().Single(x => x.Node == "node-3").State == NodeState.Dead);
            Assert(registry.ClaimRecoveries(two.Node, two.Epoch, []).Single().Ownership.Partition == 1);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    // HandoverImprovements.md §2: the handover-config document merges, bumps its revision only on
    // change, removes on null, and survives a gateway restart through the WAL.
    private static void TestHandoverConfig()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-handover-config-self-test-" + Guid.NewGuid());
        try
        {
            var emitted = new List<string>();
            using (var registry = new GatewayRegistry(directory, Admission))
            {
                registry.EventSink = (type, _, _) => emitted.Add(type);
                Assert(registry.HandoverConfigRevision == 0 && registry.HandoverConfig().Values.Count == 0);
                HandoverConfigDocument set = registry.SetHandoverConfig(new Dictionary<string, string?> { ["tee.async"] = "true", ["import.entitiesPerFrame"] = "8" });
                Assert(set.Revision == 1 && set.Values["tee.async"] == "true" && set.Values.Count == 2);
                Assert(registry.SetHandoverConfig(new Dictionary<string, string?> { ["tee.async"] = "true" }).Revision == 1);
                HandoverConfigDocument removed = registry.SetHandoverConfig(new Dictionary<string, string?> { ["import.entitiesPerFrame"] = null });
                Assert(removed.Revision == 2 && !removed.Values.ContainsKey("import.entitiesPerFrame"));
                Assert(registry.SetHandoverConfig(new Dictionary<string, string?> { ["never.set"] = null }).Revision == 2);
                Assert(emitted.Count(x => x == "handover-config.updated") == 2);
                try { registry.SetHandoverConfig(new Dictionary<string, string?> { [""] = "1" }); Assert(false); }
                catch (ArgumentException) { }
            }
            using (var reopened = new GatewayRegistry(directory, Admission))
            {
                HandoverConfigDocument document = reopened.HandoverConfig();
                Assert(document.Revision == 2 && document.Values.Count == 1 && document.Values["tee.async"] == "true");
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void TestHandoverTelemetry()
    {
        HandoverTelemetry telemetry = new();
        HandoverTelemetry.Transfer first = telemetry.Begin("t-0", 7, "node-1", "node-2", 1, 1200, 3);
        Assert(telemetry.Stamp("t-0", "begun") is >= 0);
        Assert(telemetry.Stamp("t-0", "released") >= first.States[0].AtMs);
        Assert(telemetry.Stamp("unknown", "begun") == null);
        Assert(first.States.Count == 2 && first.States[1].State == "released");
        // Node-reported hitches file on the transfer they name; hitches outside a transfer are dropped.
        telemetry.Hitch("t-0", "node-2", "import", 312.5, DateTime.UtcNow);
        telemetry.Hitch(null, "node-2", "none", 150, DateTime.UtcNow);
        telemetry.Hitch("unknown", "node-2", "capture", 150, DateTime.UtcNow);
        Assert(first.Hitches.Count == 1 && first.Hitches[0] is { Node: "node-2", Phase: "import", Ms: 312.5 });
        // The ring evicts oldest-first and forgets evicted ids.
        for (int i = 1; i <= 600; i++)
            telemetry.Begin($"t-{i}", (ulong)i, "node-1", "node-2", 0, null, null);
        Assert(telemetry.Stamp("t-0", "late") == null);
        object[] snapshot = telemetry.Snapshot();
        Assert(snapshot.Length == 512);
        Assert(telemetry.Stamp("t-600", "released") != null);
    }


    // Golden fixtures produced by the real VRage.Network serializer (VRage/MySerializer over
    // BitStream) for the two messages the Gateway decodes/encodes itself. A layout drift after a
    // DS update fails these loudly - same class of check as the type-table hash at node join.
    // The final byte of each fixture carries padding bits beyond the message's bit length, which
    // vanilla leaves uninitialized, so comparisons mask it off.
    private static readonly byte[] ClientConnectedFixture =
    [
        0xCE, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x01, 0x0E, 0x00, 0x68, 0xD7, 0xA9, 0x0F, 0x01, 0x00,
        0x10, 0x01, 0xD9, 0xC0, 0x2A, 0x6D, 0x8D, 0xEE, 0x4D, 0x6E, 0xC1, 0x14, 0x5D, 0x59, 0x58, 0x5B,
        0xF5, 0x80, 0x01, 0x85, 0x08, 0x8C, 0x0F, 0x93, 0x16, 0x9A, 0x1D, 0xA1, 0x24, 0xA8, 0x2B, 0xAF,
        0x32, 0xB6, 0x39, 0xBD, 0x40, 0xC4, 0x47, 0xCB, 0x4E, 0xD2, 0x55, 0xD9, 0x5C, 0xE0, 0x63, 0xE7,
        0x6A, 0xEE, 0x71, 0xF5, 0x78, 0xFC, 0x7F, 0x83, 0x06, 0x8A, 0x0D, 0x91, 0x14, 0x98, 0x1B, 0x9F,
        0x22, 0xA6, 0x29, 0xAD, 0x30, 0xB4, 0x37, 0xBB, 0x3E, 0xC2, 0x45, 0xC9, 0x4C, 0xD0, 0x53, 0xD7,
        0x5A, 0xDE, 0x61, 0xE5, 0x68, 0xEC, 0x6F, 0xF3, 0x76, 0xFA, 0x7D, 0x81, 0x04, 0x88, 0x0B, 0x8F,
        0x12, 0x96, 0x19, 0x9D, 0x20, 0xA4, 0x27, 0xAB, 0x2E, 0xB2, 0x35, 0xB9, 0x3C, 0xC0, 0x43, 0xC7,
        0x4A, 0xCE, 0x51, 0xD5, 0x58, 0xDC, 0x5F, 0xE3, 0x66, 0xEA, 0x6D, 0xF1, 0x74, 0xF8, 0x7B, 0xFF,
        0x02, 0x86, 0x09, 0x8D, 0x10, 0x94, 0x17, 0x9B, 0x1E, 0xA2, 0x25, 0xA9, 0x2C, 0xB0, 0x33, 0xB7,
        0x3A, 0xBE, 0x41, 0xC5, 0x48, 0xCC, 0x4F, 0xD3, 0x56, 0xDA, 0x5D, 0xE1, 0x64, 0xE8, 0x6B, 0xEF,
        0x72, 0xF6, 0x79, 0xFD, 0x00, 0x84, 0x07, 0x8B, 0x0E, 0x92, 0x15, 0x99, 0x1C, 0xA0, 0x23, 0xA7,
        0x2A, 0xAE, 0x31, 0xB5, 0x38, 0xBC, 0x3F, 0xC3, 0x46, 0xCA, 0x4D, 0xD1, 0x54, 0xD8, 0x5B, 0xDF,
        0x62, 0xE6, 0x69, 0xED, 0x70, 0xF4, 0x77, 0xFB, 0x7E, 0x82, 0x05, 0x89, 0x0C, 0x90, 0x13, 0x97,
        0x1A, 0x9E, 0x21, 0xA5, 0x28, 0xAC, 0x2F, 0xB3, 0x36, 0xBA, 0x3D, 0xC1, 0x44, 0xC8, 0x4B, 0xCF,
        0x52, 0xD6, 0x59, 0xDD, 0x60, 0xE4, 0x67, 0xEB, 0x6E, 0xF2, 0x75, 0xF9, 0x7C, 0x80, 0x03, 0x87,
        0x0A, 0x8E, 0x11, 0x95, 0x18, 0x9C, 0x1F, 0xA3, 0x26, 0xAA, 0x2D, 0x31,
    ];
    private static readonly byte[] BareClientConnectedFixture =
    [
        0xCE, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x01, 0x0E, 0x00, 0xD2, 0x02, 0x96, 0x49, 0x00, 0x00,
        0x00, 0x00, 0x86,
    ];
    private static readonly byte[] JoinResultFixture =
    [
        0xCE, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x01, 0x0C, 0x00, 0x68, 0xD7, 0xA9, 0x0F, 0x01, 0x00,
        0x10, 0x01, 0xA7,
    ];
    private static readonly byte[] JoinResultOkFixture =
    [
        0xCE, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x01, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00,
    ];

    private static void TestAdmission()
    {
        var book = new AdmissionBook(new AdmissionSettings(MemberLimit: 2, FamilySharing: false,
            Banned: [7], Administrators: [8], Reserved: [9]));
        Assert(book.Reject(1, 1, 0, false, false) == null);
        Assert(book.Reject(7, 7, 0, false, false) == JoinResult.BannedByAdmins);
        Assert(book.Reject(1, 2, 0, false, false) == JoinResult.FamilySharing);
        Assert(book.Reject(1, 1, 0, true, false) == JoinResult.AlreadyJoined);
        Assert(book.Reject(1, 1, 2, false, false) == JoinResult.ServerFull);
        Assert(book.Reject(8, 8, 2, false, false) == null); // admins bypass the cap
        Assert(book.Reject(9, 9, 2, false, false) == null); // so do reserved ids
        Assert(book.Reject(1, 1, 0, false, true) == JoinResult.ProfilingNotAllowed);
        book.Kick(1);
        Assert(book.Reject(1, 1, 0, false, false) == JoinResult.KickedRecently);
        book.Ban(2, true);
        Assert(book.Reject(2, 2, 0, false, false) == JoinResult.BannedByAdmins);
        book.Ban(2, false);
        Assert(book.Reject(2, 2, 0, false, false) == null);
        // Tag shape, not the version value - taken from the constant so a version bump never
        // has to touch this assertion.
        string version = DirectTransportConstants.VanillaVersion;
        Assert(book.GameTags(version, acceptAll: false)
            == $"groupId0 version{AdmissionBook.BuildNumber(version)} datahash mods0 gamemodeS1-1-1-1 view3000 modsTotalSize0");
        Assert(book.GameTags(version, acceptAll: true).EndsWith(" acceptall1"));
        // #1529: vanilla advertises MyVersion.ToString(), the build number; the mod total is the measured one once known
        Assert(AdmissionBook.BuildNumber("1.210.014") == "1210014" && AdmissionBook.BuildNumber("odd") == "odd");
        var modded = new AdmissionBook(new AdmissionSettings(GameMode: "S10-3-3-3", ModCount: 2, ModIds: [1, 2]));
        modded.ModsTotalSize = 1714928497;
        Assert(modded.GameTags("1.210.014", acceptAll: false)
            == "groupId0 version1210014 datahash mods2 gamemodeS10-3-3-3 view3000 modsTotalSize1714928497");
        // SE-1076: the server details blob as MyCachedServerItem.SendSettingsToSteam splits it, 93 bytes per rule
        Assert(!modded.ServerDataRules().Any());
        byte[] blob = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        Assert(modded.SetServerData(blob) && !modded.SetServerData([.. blob]));
        (string Key, string Value)[] rules = [.. modded.ServerDataRules()];
        Assert(rules.Select(rule => rule.Key).SequenceEqual(["sc", "sc0", "sc1", "sc2"]) && rules[0].Value == "200");
        Assert(rules.Skip(1).SelectMany(rule => Convert.FromBase64String(rule.Value)).SequenceEqual(blob));
        Assert(Convert.FromBase64String(rules[3].Value).Length == 14);

        // Security.md §9 item 1: every Steam verdict has to reach the client as the JoinResult
        // vanilla shows for it, and anything Valve adds later must fail closed as TicketInvalid.
        Assert(SteamFrontend.Verdict(EAuthSessionResponse.k_EAuthSessionResponseOK) == JoinResult.OK);
        Assert(SteamFrontend.Verdict(EAuthSessionResponse.k_EAuthSessionResponseUserNotConnectedToSteam) == JoinResult.UserNotConnected);
        Assert(SteamFrontend.Verdict(EAuthSessionResponse.k_EAuthSessionResponseNoLicenseOrExpired) == JoinResult.NoLicenseOrExpired);
        Assert(SteamFrontend.Verdict(EAuthSessionResponse.k_EAuthSessionResponseVACBanned) == JoinResult.VACBanned);
        Assert(SteamFrontend.Verdict(EAuthSessionResponse.k_EAuthSessionResponseLoggedInElseWhere) == JoinResult.LoggedInElseWhere);
        Assert(SteamFrontend.Verdict(EAuthSessionResponse.k_EAuthSessionResponseVACCheckTimedOut) == JoinResult.VACCheckTimedOut);
        Assert(SteamFrontend.Verdict(EAuthSessionResponse.k_EAuthSessionResponseAuthTicketCanceled) == JoinResult.TicketCanceled);
        Assert(SteamFrontend.Verdict(EAuthSessionResponse.k_EAuthSessionResponseAuthTicketInvalidAlreadyUsed) == JoinResult.TicketAlreadyUsed);
        Assert(SteamFrontend.Verdict(EAuthSessionResponse.k_EAuthSessionResponseAuthTicketInvalid) == JoinResult.TicketInvalid);
        Assert(SteamFrontend.Verdict(EAuthSessionResponse.k_EAuthSessionResponsePublisherIssuedBan) == JoinResult.BannedByAdmins);
        Assert(SteamFrontend.Verdict((EAuthSessionResponse)99) == JoinResult.TicketInvalid);

        // Security.md §3.6: accept-all is fail-closed and never shares a public interface.
        Environment.SetEnvironmentVariable("CLUSTER_ALLOW_TEST_ACCEPT_ALL", null);
        AssertArgument(() => GatewayOptions.Parse(["--direct"]));
        AssertArgument(() => GatewayOptions.Parse([]));
        Environment.SetEnvironmentVariable("CLUSTER_ALLOW_TEST_ACCEPT_ALL", "true");
        Assert(GatewayOptions.Parse(["--direct"]).DirectFrontend);
        Assert(GatewayOptions.Parse(["--steam"]).SteamListen?.Port == DirectTransportConstants.DefaultPort);
        Assert(!GatewayOptions.Parse(["--steam", "0.0.0.0:27016"]).DirectFrontend);
        AssertArgument(() => GatewayOptions.Parse(["--steam", "0.0.0.0:27016", "--direct", "--listen", "0.0.0.0:27016"]));
        AssertArgument(() => GatewayOptions.Parse(["--direct", "--listen", "8.8.8.8:27016"]));
        Assert(GatewayOptions.Parse(["--steam", "0.0.0.0:27016", "--direct", "--listen", "127.0.0.1:27116"])
            is { DirectFrontend: true, SteamListen: not null });
        // The two frontends default to different ports, so they never collide on the defaults alone.
        Assert(GatewayOptions.Parse(["--steam", "--direct", "--listen", "127.0.0.1"])
            is { SteamListen.Port: DirectTransportConstants.DefaultPort, Listen.Port: DirectTransportConstants.DefaultClientPort });
        Environment.SetEnvironmentVariable("CLUSTER_ALLOW_TEST_ACCEPT_ALL", null);
    }

    private static void TestVanillaJoinCodec()
    {
        ClientConnectedData full = VanillaJoinCodec.DecodeClientConnected(ClientConnectedFixture);
        Assert(full.ClientId == 76561198223054696);
        Assert(full.ServiceName == "Steam" && full.Name == "Viktor");
        Assert(!full.IsAdmin && full.Join && full.ExperimentalMode && !full.IsProfiling);
        Assert(full.Token != null && full.Token.Length == 234);
        Assert(full.Token!.Select((value, index) => value == (byte)(index * 7 + 3)).All(match => match));

        ClientConnectedData bare = VanillaJoinCodec.DecodeClientConnected(BareClientConnectedFixture);
        Assert(bare.ClientId == 1234567890 && bare.ServiceName == null && bare.Name == null && bare.Token == null);
        Assert(bare.IsAdmin && bare.IsProfiling && !bare.Join && !bare.ExperimentalMode);

        AssertThrows(() => VanillaJoinCodec.DecodeClientConnected(ClientConnectedFixture.AsSpan(0, 12)));
        AssertThrows(() => VanillaJoinCodec.DecodeClientConnected(BareClientConnectedFixture.AsSpan(0, 10)));

        AssertFrame(VanillaJoinCodec.EncodeJoinResultFrame(JoinResult.BannedByAdmins, serverExperimental: true,
            admin: 76561198223054696), JoinResultFixture, usedBitsInLastByte: 6);
        AssertFrame(VanillaJoinCodec.EncodeJoinResultFrame(JoinResult.OK), JoinResultOkFixture, usedBitsInLastByte: 6);
    }

    /// <summary>
    /// The pin half of the keystone deadlock: a client route may outvote a "no clients"
    /// node report only inside the settling grace, never forever. Before the fix, a
    /// partition whose report kept contradicting its route deferred every handover for
    /// the life of the cluster (w19 2026-08-19: 24 of 26 rotator move refusals were
    /// route pins, and every pinned row that outlived its node's drain became a stranded
    /// partition).
    /// </summary>
    /// <summary>
    /// The retirement index must agree with the scan it replaced on every partition, including
    /// the case that motivated the scan's "latest record wins" rule: an id consumed by one
    /// operation and RE-CREATED as a later one's output is live, not retired (judging by Any()
    /// retired 16 live partitions in 2026-08-11). Differential, so the index cannot drift from
    /// the semantics without this failing.
    /// </summary>
    /// <summary>
    /// RecoveryPending() gates balance initiation, so its release condition is load-bearing: the
    /// first -glb2 boot (2026-08-24 17:23) paused balancing for the life of the process because
    /// eight RETIRED tombstone rows still carried a stale Recovering flag - a lease expiry returns
    /// a mid-recovery partition to unowned WITHOUT clearing Recovering, and retirement never
    /// touches the flag either. A retired partition can never be recovered, so it is never pending.
    /// </summary>
    /// <summary>
    /// T-0222: ReplicaWork picked by SaveId, whose CompareTo ignores the partition, so a quiet partition's
    /// generation-1 sequence-1 save ranked below every busy partition's save and was never replicated.
    /// The order is now: a save whose holder is draining, then a partition's newest save ahead of the
    /// ones it supersedes, then the most recent first. Oldest-first (the first fix) starved every save
    /// made after the 2026-09-23 07:03 deploy: the ~20k-save backlog the old order left behind took
    /// all 9933 replica copies of the next hour. Newest-first drains that backlog with the spare
    /// capacity instead (new newest-of-partition saves were ~18/min against ~158 copies/min).
    /// </summary>
    /// <summary>
    /// T-0222: every polling node was handed the same top-ranked save (node-1 and node-4 both directed to
    /// 181545:1:1 at 11:44), so copies landed three to five times over while other saves kept one. A
    /// handed-out save is reserved for its replicator for a minute: the next poller gets the next save,
    /// the replicator itself may ask again, and the reservation lapses if it never registers.
    /// </summary>
    private static void TestReplicaWorkSpread()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        try
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(600));
            NodeRegistration one = Register(registry, "one", "ep1", Guid.NewGuid());
            NodeRegistration two = Register(registry, "two", "ep2", Guid.NewGuid());
            NodeRegistration three = Register(registry, "three", "ep3", Guid.NewGuid());
            PartitionOwnership a = registry.ClaimPartition(1, one.Node, one.Epoch, 0);
            registry.RegisterSave(new SaveId(1, a.Generation, 1), one.Node, one.Epoch, SaveKind.Periodic, 1, "a", 1, "one/a", "http://one");
            now += TimeSpan.FromSeconds(1);
            PartitionOwnership b = registry.ClaimPartition(2, one.Node, one.Epoch, 0);
            registry.RegisterSave(new SaveId(2, b.Generation, 1), one.Node, one.Epoch, SaveKind.Periodic, 1, "b", 2, "one/b", "http://one");
            ReplicaDirective toTwo = registry.ReplicaWork(two.Node, two.Epoch).Single();
            ReplicaDirective toThree = registry.ReplicaWork(three.Node, three.Epoch).Single();
            Assert(toTwo.SaveId.Partition != toThree.SaveId.Partition);   // two pollers, two different saves
            Assert(registry.ReplicaWork(two.Node, two.Epoch).Single().SaveId.Partition == toTwo.SaveId.Partition);   // its own reservation does not hide it
            registry.RegisterHolder(toTwo.SaveId, two.Node, two.Epoch, 1, toTwo.Checksum, "two/x", "http://two");
            // three never registers its copy: after the reservation lapses, two may take that save.
            Assert(registry.ReplicaWork(two.Node, two.Epoch).Length == 0);
            now += TimeSpan.FromSeconds(61);
            Assert(registry.ReplicaWork(two.Node, two.Epoch).Single().SaveId.Partition == toThree.SaveId.Partition);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// T-0227: the replica queue is kept incrementally. A save catalogued after the queue was built is
    /// served at once, without a full ranking; a save that reaches two holders drops out; a node-state
    /// change (here a drain) rebuilds it. Every answer here and in every other section is also checked
    /// against the brute-force choice, and the queue against a full ranking after every write.
    /// </summary>
    private static void TestReplicaQueue()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        try
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(600));
            NodeRegistration one = Register(registry, "one", "ep1", Guid.NewGuid());
            NodeRegistration two = Register(registry, "two", "ep2", Guid.NewGuid());
            NodeRegistration three = Register(registry, "three", "ep3", Guid.NewGuid());
            PartitionOwnership a = registry.ClaimPartition(1, one.Node, one.Epoch, 0);
            registry.RegisterSave(new SaveId(1, a.Generation, 1), one.Node, one.Epoch, SaveKind.Periodic, 1, "a", 1, "one/a", "http://one");
            ReplicaDirective first = registry.ReplicaWork(two.Node, two.Epoch).Single();
            Assert(first.SaveId.Partition == 1 && registry.ReplicaRebuilds == 1);
            PartitionOwnership b = registry.ClaimPartition(2, one.Node, one.Epoch, 0);
            registry.RegisterSave(new SaveId(2, b.Generation, 1), one.Node, one.Epoch, SaveKind.Periodic, 1, "b", 2, "one/b", "http://one");
            // The new save is served straight away, from the incremental queue.
            ReplicaDirective second = registry.ReplicaWork(three.Node, three.Epoch).Single();
            Assert(second.SaveId.Partition == 2 && registry.ReplicaRebuilds == 1);
            // Save 1 reaches two holders and leaves the queue; save 2 is reserved for three.
            registry.RegisterHolder(first.SaveId, two.Node, two.Epoch, 1, "a", "two/a", "http://two");
            Assert(registry.ReplicaWork(two.Node, two.Epoch).Length == 0 && registry.ReplicaRebuilds == 1);
            // Draining the only holder of save 2 is a node-state change: the queue is rebuilt.
            registry.CloseNode(one.Node, one.Epoch, TimeSpan.FromSeconds(5));
            Assert(registry.ReplicaWork(three.Node, three.Epoch).Single().SaveId.Partition == 2 && registry.ReplicaRebuilds == 2);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Heap shrink: a retired partition that no live cover and no snapshot needs has its saves moved to
    /// the archive table - out of memory, never deleted - and survives a restart that way; one a live
    /// cover still needs, or a snapshot pins, stays. A live partition that falls back on an archived
    /// ancestor brings it back, and keeps it while it is needed.
    /// </summary>
    private static void TestSaveArchive()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        try
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            SaveId parentSave, pinnedSave, neededSave;
            ulong first;
            ulong[] done, open, pinnedOut;
            using (var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(600)))
            {
                NodeRegistration one = Register(registry, "one", "ep1", Guid.NewGuid());
                NodeRegistration two = Register(registry, "two", "ep2", Guid.NewGuid());
                SaveId Save(ulong partition, int sequence, long time)
                {
                    long generation = registry.Partitions().Single(x => x.Partition == partition).Generation;
                    var id = new SaveId(partition, generation, sequence);
                    registry.RegisterSave(id, one.Node, one.Epoch, SaveKind.Periodic, 1, $"c{partition}.{sequence}", time, $"one/{partition}.{sequence}", "http://one");
                    return id;
                }
                LineageRecord Split(ulong partition, long time, out ulong[] outputs)
                {
                    long generation = registry.Partitions().Single(x => x.Partition == partition).Generation;
                    outputs = registry.AllocatePartitionIds(one.Node, one.Epoch, 2);
                    return registry.ApplyLineage($"split-{partition}", LineageKind.Split, one.Node, one.Epoch, [new(partition, generation)], outputs,
                        new() { [outputs[0]] = [(long)partition * 10 + 1], [outputs[1]] = [(long)partition * 10 + 2] }, time);
                }
                ulong[] roots = registry.AllocatePartitionIds(one.Node, one.Epoch, 3);
                first = roots[0]; ulong second = roots[1], third = roots[2];
                // first: split, both outputs saved since -> nothing live needs it: archived.
                registry.ClaimPartition(first, one.Node, one.Epoch, 0);
                Save(first, 1, 10);
                parentSave = Save(first, 2, 11);
                Split(first, 20, out done);
                foreach (ulong output in done) Save(output, 1, 30);
                // second: split, one output never saved -> its cover is 2's save: kept.
                registry.ClaimPartition(second, one.Node, one.Epoch, 0);
                neededSave = Save(second, 1, 12);
                Split(second, 21, out open);
                Save(open[0], 1, 31);
                // third: split and fully superseded, but a snapshot pins its save: kept.
                registry.ClaimPartition(third, one.Node, one.Epoch, 0);
                pinnedSave = Save(third, 1, 13);
                registry.CreateSnapshot("pin", SnapshotKind.Online, 14, [pinnedSave], []);
                Split(third, 22, out pinnedOut);
                foreach (ulong output in pinnedOut) Save(output, 1, 32);

                // Retention as before: the retired partition keeps its newest save and drops the older one.
                // Its pending delete holds the archive back until the holder acknowledges it.
                DeleteDirective older = registry.PlanRetention().Single(x => x.SaveId.Partition == first);
                Assert(older.SaveId != parentSave && registry.ArchivedPartitionCount == 0);
                Assert(registry.AcknowledgeDelete(older.SaveId, older.Location));
                Assert(registry.PlanRetention().All(x => x.SaveId.Partition != first));   // archived, never deleted
                SaveRegistration[] saves = registry.Saves();
                Assert(saves.All(x => x.SaveId.Partition != first) && registry.ArchivedPartitionCount == 1);
                Assert(saves.Any(x => x.SaveId == neededSave) && saves.Any(x => x.SaveId == pinnedSave));
                // Live covers are untouched, and asking them reads nothing back.
                Assert(registry.ResolveCover(done[0]).Single().Save.SaveId.Partition == done[0]);
                Assert(registry.ResolveCover(open[1]).Single().Save.SaveId == neededSave);
                Assert(registry.ArchiveRehydrations == 0);
                // A second pass is idle: nothing left to archive, nothing re-read.
                registry.PlanRetention();
                Assert(registry.ArchivedPartitionCount == 1 && registry.ArchiveRehydrations == 0);
                // The owner reports the only copy of done[0]'s save corrupt: its cover now falls back on
                // the split parent, which comes back from the archive whole.
                SaveId lost = registry.Saves().Single(x => x.SaveId.Partition == done[0]).SaveId;
                registry.ReportCorruption(lost, one.Node, two.Node, two.Epoch);
                SaveCover fallback = registry.ResolveCover(done[0]).Single();
                Assert(fallback.Save.SaveId == parentSave && fallback.EntityIds!.SequenceEqual([(long)first * 10 + 1]));
                Assert(registry.ArchiveRehydrations == 1 && registry.ArchivedPartitionCount == 0);
                Assert(registry.Saves().Count(x => x.SaveId.Partition == first) == 1);
                // Needed again, so the next pass keeps it in memory.
                registry.PlanRetention();
                Assert(registry.ArchivedPartitionCount == 0 && registry.Saves().Any(x => x.SaveId == parentSave));
                // A fresh copy of done[0] makes it redundant again: archived on the next pass.
                registry.RegisterSave(new SaveId(done[0], lost.Generation, 2), one.Node, one.Epoch, SaveKind.Periodic, 1, "fresh", 40, "one/fresh", "http://one");
                registry.PlanRetention();
                Assert(registry.ArchivedPartitionCount == 1 && registry.Saves().All(x => x.SaveId.Partition != first));
                registry.Checkpoint();
            }
            // A restart keeps the archive out of memory and the covers as they were.
            using (var reopened = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(600)))
            {
                Assert(reopened.ArchivedPartitionCount == 1 && reopened.Saves().All(x => x.SaveId.Partition != first));
                Assert(reopened.ResolveCover(done[1]).Single().Save.SaveId.Partition == done[1]);
                Assert(reopened.ArchiveRehydrations == 0);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// T-0229 relay IPC transport: Hello both ways, request/answer correlation, relay-initiated
    /// requests reach the handler and are answered on their correlation, binary bodies, a relay that
    /// goes silent is dropped after the missed-heartbeat limit, and a new relay is then accepted.
    /// </summary>
    /// <summary>
    /// T-0296 r3: a JoinRequest is answered on the thread pool (admission takes the registry lock). When the relay
    /// IPC is lost and gateway-rs reconnects before that answer goes out, the accept used to be SENT to the new
    /// connection, which never asked: gateway-rs dropped it, and ClusterRegistry kept a session the relay does not
    /// have - every later join of that player was refused as a second join. The answer must fail instead, so the
    /// join's own failure path removes the session.
    /// </summary>
    private static void TestRelayIpcStaleAnswer()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("relay-ipc-stale-answer: " + what); }
        string path = Path.Combine(Path.GetTempPath(), $"relay-ipc-stale-selftest-{Environment.ProcessId}.sock");
        using var server = new RelayIpcServer(path, heartbeat: TimeSpan.FromSeconds(30), missedLimit: 3);
        var ready = new SemaphoreSlim(0);
        var lost = new SemaphoreSlim(0);
        var joins = new System.Collections.Concurrent.BlockingCollection<RelayIpcMessage>();
        server.PeerReady += () => ready.Release();
        server.PeerLost += _ => lost.Release();
        server.Message += message => { if (message.Type == RelayIpcType.JoinRequest) joins.Add(message); };
        server.Start();
        static byte[] Frame(ushort type, uint correlation, object body)
        {
            byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body);
            byte[] frame = new byte[6 + json.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(frame, type);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2), correlation);
            json.CopyTo(frame, 6);
            return frame;
        }
        Socket Connect()
        {
            var socket = new Socket(AddressFamily.Unix, (SocketType)5, ProtocolType.Unspecified) { ReceiveTimeout = 5000 };
            socket.Connect(new UnixDomainSocketEndPoint(path));
            socket.Send(Frame(RelayIpcType.Hello, 0, new { version = RelayIpcServer.Version, role = "relay", pid = 1, build = "selftest" }));
            Check(ready.Wait(5000), "the relay connects");
            return socket;
        }
        // Every JoinAnswer the socket holds right now (the registry's Hello is skipped).
        static List<uint> Answers(Socket socket)
        {
            List<uint> answers = [];
            byte[] buffer = new byte[RelayIpcServer.MaximumMessage];
            socket.ReceiveTimeout = 300;
            while (true)
            {
                int read;
                try { read = socket.Receive(buffer); }
                catch (SocketException) { return answers; }
                if (read < 6) return answers;
                if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer) == RelayIpcType.JoinAnswer)
                    answers.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(2)));
            }
        }
        try
        {
            RelayIpcMessage queued;
            using (Socket first = Connect())
            {
                first.Send(Frame(RelayIpcType.JoinRequest, 41, new { clientId = 7, session = 70 }));
                Check(joins.TryTake(out queued, 5000), "the join request of the first connection arrives");
                // Control: answered while its connection lives, the answer is delivered.
                first.Send(Frame(RelayIpcType.JoinRequest, 42, new { clientId = 8, session = 80 }));
                Check(joins.TryTake(out RelayIpcMessage live, 5000), "the control join request arrives");
                server.Answer(live, RelayIpcType.JoinAnswer, new { decision = "accept" });
                Check(Answers(first).SequenceEqual([42u]), "an answer on the request's own connection is delivered");
            }
            Check(lost.Wait(5000), "the first connection is lost");
            using Socket second = Connect();
            bool refused = false;
            try { server.Answer(queued, RelayIpcType.JoinAnswer, new { decision = "accept" }); }
            catch (IOException) { refused = true; }
            List<uint> delivered = Answers(second);
            Check(refused && delivered.Count == 0, $"the queued accept of the lost connection fails instead of reaching the new one "
                + $"(threw={refused}, delivered to the new connection: [{string.Join(",", delivered)}])");
        }
        finally { server.Dispose(); }
        Console.WriteLine("relay-ipc-stale-answer: an answer to a request of a lost relay connection fails; the new connection never sees it");
    }

    /// <summary>
    /// #1138: a send that races the server's disposal fails with IOException, which every caller takes as "no relay
    /// connected". It used to fail with the socket's ObjectDisposedException: a queued OnRelayLinkState callback that ran
    /// OnLinkConnected after the relay was gone killed the gateway from the thread pool. Here a 4 MB frame blocks in the
    /// middle of its fragments (the relay reads nothing), and the server is disposed under it.
    /// </summary>
    private static void TestRelayIpcDisposeMidSend()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("relay-ipc-dispose-mid-send: " + what); }
        string path = Path.Combine(Path.GetTempPath(), $"relay-ipc-dispose-selftest-{Environment.ProcessId}.sock");
        var server = new RelayIpcServer(path, heartbeat: TimeSpan.FromSeconds(30), missedLimit: 3);
        var ready = new SemaphoreSlim(0);
        server.PeerReady += () => ready.Release();
        server.Start();
        using var relay = new Socket(AddressFamily.Unix, (SocketType)5, ProtocolType.Unspecified);
        relay.Connect(new UnixDomainSocketEndPoint(path));
        byte[] hello = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { version = RelayIpcServer.Version, role = "relay", pid = 1, build = "selftest" });
        byte[] frame = new byte[6 + hello.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(frame, RelayIpcType.Hello);
        hello.CopyTo(frame, 6);
        relay.Send(frame);
        Check(ready.Wait(5000), "the relay connects");
        static Exception? Attempt(Action send)
        {
            try { send(); return null; }
            catch (Exception error) { return error; }
        }
        byte[] big = new byte[4_000_000];
        Task<Exception?> sending = Task.Run(() => Attempt(() => server.Send(RelayIpcType.NodeGlobal, 0, big)));
        Check(!sending.Wait(300), "the 4 MB frame blocks while the relay reads nothing");
        server.Dispose();
        Check(sending.Wait(5000), "the blocked send ends when the server is disposed");
        Check(sending.Result is IOException, $"the blocked send fails with IOException, not {sending.Result?.GetType().Name ?? "success"}: {sending.Result?.Message}");
        Exception? after = Attempt(() => server.Send(RelayIpcType.SessionUpdate, 0, new { session = 1 }));
        Check(after is IOException, $"a send after disposal fails with IOException, not {after?.GetType().Name ?? "success"}");
        Console.WriteLine($"relay-ipc-dispose-mid-send: a send blocked mid-frame at disposal and one after it both fail with IOException ({sending.Result!.Message})");
    }

    private static void TestRelayIpc()
    {
        string path = Path.Combine(Path.GetTempPath(), $"relay-ipc-selftest-{Environment.ProcessId}.sock");
        using var server = new RelayIpcServer(path, heartbeat: TimeSpan.FromMilliseconds(100), missedLimit: 3);
        var ready = new SemaphoreSlim(0);
        var lost = new SemaphoreSlim(0);
        var received = new System.Collections.Concurrent.BlockingCollection<RelayIpcMessage>();
        server.PeerReady += () => ready.Release();
        server.PeerLost += _ => lost.Release();
        server.Message += message =>
        {
            if (message.Type == RelayIpcType.JoinRequest)
                server.Answer(message, RelayIpcType.JoinAnswer, new { decision = "hold", holdSeconds = 7 });
            else received.Add(message);
        };
        server.Start();
        Assert((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0);

        static byte[] Frame(ushort type, uint correlation, byte[] body)
        {
            byte[] frame = new byte[6 + body.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(frame, type);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2), correlation);
            body.CopyTo(frame, 6);
            return frame;
        }
        static RelayIpcMessage Read(Socket socket, ushort wanted)
        {
            byte[] buffer = new byte[RelayIpcServer.MaximumMessage];
            while (true)
            {
                int read = socket.Receive(buffer);
                ushort type = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer);
                if (type == wanted)
                    return new(type, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(2)), buffer.AsSpan(6, read - 6).ToArray());
            }
        }
        Socket Connect()
        {
            var socket = new Socket(AddressFamily.Unix, (SocketType)5, ProtocolType.Unspecified) { ReceiveTimeout = 5000 };
            socket.Connect(new UnixDomainSocketEndPoint(path));
            Assert(Read(socket, RelayIpcType.Hello).Json.GetProperty("role").GetString() == "registry");
            socket.Send(Frame(RelayIpcType.Hello, 0, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { version = RelayIpcServer.Version, role = "relay", pid = 1, build = "selftest" })));
            Assert(ready.Wait(5000) && server.Connected && server.PeerBuild == "selftest");
            return socket;
        }

        using (Socket relay = Connect())
        {
            // The registry asks, the relay answers on the same correlation.
            Task<RelayIpcMessage> status = server.RequestAsync(RelayIpcType.Status, new { }, TimeSpan.FromSeconds(5));
            RelayIpcMessage asked = Read(relay, RelayIpcType.Status);
            Assert(asked.Correlation != 0);
            relay.Send(Frame(RelayIpcType.Status, asked.Correlation, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { sessions = Array.Empty<object>() })));
            Assert(status.Wait(5000) && status.Result.Json.GetProperty("sessions").GetArrayLength() == 0);
            // The relay asks, the handler answers on the relay's correlation.
            relay.Send(Frame(RelayIpcType.JoinRequest, 77, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { clientId = 5UL, session = 1UL, service = "DirectTransport" })));
            RelayIpcMessage answer = Read(relay, RelayIpcType.JoinAnswer);
            Assert(answer.Correlation == 77 && answer.Json.GetProperty("holdSeconds").GetInt32() == 7);
            // A one-way binary message reaches the handler intact.
            relay.Send(Frame(RelayIpcType.NodeGlobal, 0, [1, 2, 3, 250]));
            Assert(received.TryTake(out RelayIpcMessage global, 5000) && global.Type == RelayIpcType.NodeGlobal && global.Body.Span.SequenceEqual(new byte[] { 1, 2, 3, 250 }));
            // A frame above FragmentAbove (a live world's full global-state snapshot is 0.5-4 MB) crosses
            // in fragments both ways; version 1 dropped it. Relay -> registry: two frames interleaved,
            // as gateway-rs's threads send them, then a small message that must come after both.
            byte[] big = new byte[4_136_274];
            for (int i = 0; i < big.Length; i++) big[i] = (byte)(i * 7);
            byte[] other = new byte[RelayIpcServer.MaximumMessage + 1];
            other.AsSpan().Fill(9);
            byte[][] bigParts = RelayIpcServer.Fragments(40, Frame(RelayIpcType.NodeGlobal, 0, big)).ToArray();
            byte[][] otherParts = RelayIpcServer.Fragments(41, Frame(RelayIpcType.NodePlugin, 0, other)).ToArray();
            Assert(bigParts.Length > otherParts.Length && otherParts.Length >= 2 && bigParts.Concat(otherParts).All(f => f.Length <= RelayIpcServer.FragmentAbove));
            for (int i = 0; i < bigParts.Length; i++)
            {
                relay.Send(bigParts[i]);
                if (i < otherParts.Length) relay.Send(otherParts[i]);
            }
            relay.Send(Frame(RelayIpcType.NodeGlobal, 0, [42]));
            Assert(received.TryTake(out RelayIpcMessage firstDone, 5000) && firstDone.Type == RelayIpcType.NodePlugin && firstDone.Body.Span.SequenceEqual(other));
            Assert(received.TryTake(out RelayIpcMessage bigDone, 5000) && bigDone.Type == RelayIpcType.NodeGlobal && bigDone.Body.Span.SequenceEqual(big));
            Assert(received.TryTake(out RelayIpcMessage after, 5000) && after.Body.Span.SequenceEqual(new byte[] { 42 }));
            // A fragment that skips ahead drops that frame loudly, and the next one still passes.
            relay.Send(bigParts[0]);
            relay.Send(bigParts[2]);
            relay.Send(Frame(RelayIpcType.NodeGlobal, 0, [43]));
            Assert(received.TryTake(out RelayIpcMessage next, 5000) && next.Body.Span.SequenceEqual(new byte[] { 43 }) && server.FragmentFailures == 1);
            // Registry -> relay: the same frame, reassembled as gateway-rs does.
            // (Sent from another thread: 4 MB does not fit the socket buffer, so Send blocks until read.)
            Task sending = Task.Run(() => server.Send(RelayIpcType.NodeGlobal, 0, big));
            var reassembler = new RelayIpcReassembler(null);
            byte[]? whole = null;
            while (whole == null) whole = reassembler.Push(Read(relay, RelayIpcType.Fragment).Body.Span);
            Assert(sending.Wait(5000));
            Assert(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(whole) == RelayIpcType.NodeGlobal && whole.AsSpan(6).SequenceEqual(big));
            Assert(server.FragmentedSent == 1 && server.FragmentedReceived == 2 && reassembler.Failures == 0);
            // Gathered sends (prefix + payload, never built whole) put the same bytes on the wire,
            // small and fragmented, as the Gateway's tunnel frames need them.
            byte[] prefix = [1, 5, 0, (byte)'n', (byte)'o', (byte)'d', (byte)'e', (byte)'1'];
            foreach (int size in new[] { 1000, 3_000_017 })
            {
                byte[] payload = new byte[size];
                for (int i = 0; i < size; i++) payload[i] = (byte)(i * 13 + 1);
                Task gathered = Task.Run(() => server.SendBinary(RelayIpcType.NodeGlobal, 9, prefix, payload));
                byte[] expected = Frame(RelayIpcType.NodeGlobal, 9, [.. prefix, .. payload]);
                byte[]? got = null;
                if (size < RelayIpcServer.FragmentAbove)
                {
                    RelayIpcMessage small = Read(relay, RelayIpcType.NodeGlobal);
                    got = Frame(small.Type, small.Correlation, small.Body.ToArray());
                }
                else
                    while (got == null) got = reassembler.Push(Read(relay, RelayIpcType.Fragment).Body.Span);
                Assert(gathered.Wait(5000) && got.AsSpan().SequenceEqual(expected));
            }
            Assert(server.FragmentedSent == 2 && reassembler.Failures == 0);
            // Silence: the relay stops sending (no heartbeats), and is dropped within the limit.
            var pending = server.RequestAsync(RelayIpcType.Status, new { }, TimeSpan.FromSeconds(10));
            Assert(lost.Wait(5000) && !server.Connected);
            Assert(pending.ContinueWith(t => t.IsFaulted).Wait(5000) && pending.IsFaulted);
        }
        // A new relay is accepted after the loss.
        using (Socket again = Connect())
            Assert(server.Connected && server.PeerLosses == 1);
    }

    private static void TestReplicaWorkOrder()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        try
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(600));
            NodeRegistration one = Register(registry, "one", "ep1", Guid.NewGuid());
            NodeRegistration two = Register(registry, "two", "ep2", Guid.NewGuid());
            NodeRegistration three = Register(registry, "three", "ep3", Guid.NewGuid());
            // Quiet partition 2: one save, generation 1 sequence 1, made FIRST.
            PartitionOwnership quiet = registry.ClaimPartition(2, one.Node, one.Epoch, 0);
            registry.RegisterSave(new SaveId(2, quiet.Generation, 1), one.Node, one.Epoch, SaveKind.Periodic, 1, "q", 1, "one/q", "http://one");
            now += TimeSpan.FromSeconds(10);
            // Busy partition 1: high sequences, made later - the old SaveId order picked these first.
            PartitionOwnership busy = registry.ClaimPartition(1, one.Node, one.Epoch, 0);
            for (int sequence = 1; sequence <= 9; sequence++)
            {
                registry.RegisterSave(new SaveId(1, busy.Generation, sequence), one.Node, one.Epoch, SaveKind.Periodic, 1, "b" + sequence, 1 + sequence, "one/b" + sequence, "http://one");
                now += TimeSpan.FromSeconds(1);
            }
            ReplicaDirective first = registry.ReplicaWork(two.Node, two.Epoch).Single();
            Assert(first.SaveId.Partition == 1 && first.SaveId.Sequence == 9);   // most recent newest-of-partition: b9, not the backlog
            registry.RegisterHolder(first.SaveId, two.Node, two.Epoch, 1, "b9", "two/b9", "http://two");
            ReplicaDirective second = registry.ReplicaWork(two.Node, two.Epoch).Single();
            Assert(second.SaveId.Partition == 2);   // the quiet save before b1..b8, which are newer but superseded
            registry.RegisterHolder(second.SaveId, two.Node, two.Epoch, 1, "q", "two/q", "http://two");
            // A quiet partition with no second copy anywhere, then a newer save of the busy partition,
            // whose predecessor b9 already has two holders: the unprotected one goes first.
            PartitionOwnership lonely = registry.ClaimPartition(4, one.Node, one.Epoch, 0);
            registry.RegisterSave(new SaveId(4, lonely.Generation, 1), one.Node, one.Epoch, SaveKind.Periodic, 1, "z", 30, "one/z", "http://one");
            now += TimeSpan.FromSeconds(5);
            registry.RegisterSave(new SaveId(1, busy.Generation, 10), one.Node, one.Epoch, SaveKind.Periodic, 1, "b10", 31, "one/b10", "http://one");
            ReplicaDirective exposed = registry.ReplicaWork(two.Node, two.Epoch).Single();
            Assert(exposed.SaveId.Partition == 4);   // not b10: partition 1 is covered by b9, partition 4 by nothing
            registry.RegisterHolder(exposed.SaveId, two.Node, two.Epoch, 1, "z", "two/z", "http://two");
            // A second covered partition saves after b10: covered saves go oldest-first, so b10 still
            // comes first - newest-first starved a quiet covered partition behind busy ones (12:57).
            now += TimeSpan.FromSeconds(5);
            registry.RegisterSave(new SaveId(2, quiet.Generation, 2), one.Node, one.Epoch, SaveKind.Periodic, 1, "q2", 32, "one/q2", "http://one");
            ReplicaDirective covered = registry.ReplicaWork(two.Node, two.Epoch).Single();
            Assert(covered.SaveId.Partition == 1 && covered.SaveId.Sequence == 10);   // then the older covered newest save
            // A save held only by a draining node jumps the queue.
            PartitionOwnership leaving = registry.ClaimPartition(3, three.Node, three.Epoch, 0);
            registry.RegisterSave(new SaveId(3, leaving.Generation, 1), three.Node, three.Epoch, SaveKind.Periodic, 1, "l", 20, "three/l", "http://three");
            registry.CloseNode(three.Node, three.Epoch, TimeSpan.FromSeconds(5));
            ReplicaDirective third = registry.ReplicaWork(two.Node, two.Epoch).Single();
            Assert(third.SaveId.Partition == 3);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// T-0217: ReplicaWork gave a Closed (draining) node partition replica directives, and the save
    /// grant it then asked for refused it ("Node 'node-N' epoch E is Closed", 409) - once per rotation
    /// on w32. A draining node must get no NEW partition directive, while an Active one still gets the
    /// same save; a directive fetched just before the close still gets its grant and registers.
    /// </summary>
    private static void TestReplicaWorkDraining()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        try
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration one = Register(registry, "one", "ep1", Guid.NewGuid());
            NodeRegistration two = Register(registry, "two", "ep2", Guid.NewGuid());
            PartitionOwnership owner = registry.ClaimPartition(1, one.Node, one.Epoch, 0);
            registry.RegisterSave(new SaveId(1, owner.Generation, 1), one.Node, one.Epoch,
                SaveKind.Periodic, 1, "hash", 1, "one/save", "http://one");
            ReplicaDirective[] active = registry.ReplicaWork(two.Node, two.Epoch);
            Assert(active.Length == 1 && active[0].SaveId.Partition == 1 && !active[0].Global);
            registry.CloseNode(two.Node, two.Epoch, TimeSpan.FromSeconds(5));
            Assert(registry.ReplicaWork(two.Node, two.Epoch).All(directive => directive.Global));
            // A directive fetched just before the close must still complete: the grant is issued to
            // the Closed requester, and the holder it registers is accepted.
            TransferGrant grant = registry.IssueGrant(new GrantRequest("save", string.Empty, 1, owner.Generation, 1,
                two.Node, two.Epoch, one.Node));
            Assert(grant.Receiver == one.Node);
            registry.RegisterHolder(new SaveId(1, owner.Generation, 1), two.Node, two.Epoch, 1, "hash", "two/save", "http://two");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void TestRecoveryPending()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        try
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration node = Register(registry, "one", "ep", Guid.NewGuid());
            PartitionOwnership two = registry.ClaimPartition(2, node.Node, node.Epoch, 0);
            Assert(!registry.RecoveryPending());   // owned with a live lease: nothing pending
            // A save keeps the released row recoverable; a saveless release is reaped outright.
            registry.RegisterSave(new SaveId(2, two.Generation, 1), node.Node, node.Epoch,
                SaveKind.Periodic, 1, "hash", 2, "one/save");
            registry.ReleasePartition(2, node.Node, node.Epoch, two.Generation);
            Assert(registry.RecoveryPending());    // unowned and claimable: pending
            registry.ClaimRecoveries(node.Node, node.Epoch, [2]);
            Assert(registry.RecoveryPending());    // recovery claimed, import in flight: pending
            now += TimeSpan.FromSeconds(11);
            registry.Heartbeat(node.Node, node.Epoch, []);
            Assert(registry.ExpireLeases() >= 1);
            // The stale-flag mechanism: expiry returned the row to unowned but left Recovering set.
            Assert(registry.GetPartitionOwner(2) is { Node: null, Recovering: true });
            Assert(registry.RecoveryPending());    // not retired, so still genuinely pending
            PartitionOwnership one = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
            registry.RecordLineage("retire-two", LineageKind.Merge,
                [new(2, registry.GetPartitionOwner(2).Generation), new(1, one.Generation)],
                [new(1, one.Generation)], null, 10);
            // The live w23 shape: a retired tombstone still flagged Recovering must NOT hold the
            // balancer paused.
            Assert(registry.GetPartitionOwner(2) is { Node: null, Recovering: true });
            Assert(!registry.RecoveryPending());

            // RotationInFlight: a regular's Activated stamp inside the settling window reads as a
            // rotation (the fresh spare's empty load is transient), outside it does not, and a
            // Closed node always does.
            Assert(registry.RotationInFlight(TimeSpan.FromSeconds(90)));    // activated 11 s ago
            now += TimeSpan.FromSeconds(100);
            registry.Heartbeat(node.Node, node.Epoch, [new(1, one.Generation)]);
            Assert(!registry.RotationInFlight(TimeSpan.FromSeconds(90)));   // settled
            registry.CloseNode(node.Node, node.Epoch, TimeSpan.FromSeconds(5));
            Assert(registry.RotationInFlight(TimeSpan.FromSeconds(90)));    // Closed node
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// T-0198: a partition whose saves are all catalogued but whose holders are gone is claimable by
    /// nobody (ResolveCover asks about holders and answers empty) and reapable by nobody (the sweep's
    /// cheap early-out only counts catalogue rows). It used to sit that way in silence, holding
    /// RecoveryPending() - and therefore the balancer - for the life of the process: sirius bench
    /// P3-PLR-01 run .3, 2026-09-20, where node-1 died before any copy of partition 3 had replicated
    /// off it. The row must still be kept (an operator may yet bring the data back) and the sweep
    /// must SAY so, once, naming the partition and the nodes that last held it.
    /// </summary>
    private static void TestStrandedPartition()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        var log = new StringWriter();
        try
        {
            Console.SetOut(log);
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration one = Register(registry, "one", "ep-one", Guid.NewGuid());
            NodeRegistration two = Register(registry, "two", "ep-two", Guid.NewGuid());
            PartitionOwnership five = registry.ClaimPartition(5, one.Node, one.Epoch, 0);
            PartitionOwnership six = registry.ClaimPartition(6, two.Node, two.Epoch, 0);
            // Partition 5's save exists, is catalogued, and "one" is its only holder - the shape a
            // cluster is in between a partition's first save and its first replication to a peer.
            // Partition 6 is the control: same story, but its holder outlives the sweep.
            // The control endpoint matters: a save registered without one is treated as reachable
            // whatever its holder is doing (Newest()'s `!HolderEndpoints.ContainsKey(holder)` arm),
            // and a node always registers its saves with one.
            registry.RegisterSave(new SaveId(5, five.Generation, 1), one.Node, one.Epoch,
                SaveKind.Periodic, 1, "hash", 2, "one/save", "http://one/control");
            registry.RegisterSave(new SaveId(6, six.Generation, 1), two.Node, two.Epoch,
                SaveKind.Periodic, 1, "hash", 2, "two/save", "http://two/control");
            registry.ReleasePartition(5, one.Node, one.Epoch, five.Generation);
            registry.ReleasePartition(6, two.Node, two.Epoch, six.Generation);

            // "one" dies: it stops heartbeating and its lease lapses, so it can no longer serve the
            // only copy of 5's save. Everything about the catalogue is unchanged.
            now += TimeSpan.FromSeconds(11);
            registry.Heartbeat(two.Node, two.Epoch, []);
            registry.ExpireLeases();
            // The auto-pick is where the silence lives: an EXPLICITLY requested partition is handed
            // out whatever its cover looks like (warm recovery of a node's own world), so only the
            // generic claim consults ResolveCover. It offers 6, whose holder is alive, and never 5 -
            // which is both the zero under test and its own coverage control.
            RecoveryClaim[] offered = registry.ClaimRecoveries(two.Node, two.Epoch, []);
            Assert(offered.Length == 1 && offered[0].Ownership.Partition == 6);

            // Inside the grace the sweep says nothing: a recovery may simply not have reached it yet.
            now += TimeSpan.FromMinutes(4);
            registry.Heartbeat(two.Node, two.Epoch, []);
            registry.ExpireLeases();
            Assert(!log.ToString().Contains("partition 5 is stranded"));

            // Past it, exactly once, naming the partition and the node that last held the save...
            now += TimeSpan.FromMinutes(2);
            registry.Heartbeat(two.Node, two.Epoch, []);
            registry.ExpireLeases();
            string said = log.ToString();
            Assert(said.Contains("partition 5 is stranded") && said.Contains("last held by one"));
            registry.Heartbeat(two.Node, two.Epoch, []);
            registry.ExpireLeases();
            Assert(said.Split("partition 5 is stranded").Length == 2
                && log.ToString().Split("partition 5 is stranded").Length == 2);

            // ...the row is KEPT, because the data may yet come back with its node...
            Assert(registry.GetPartitionOwner(5) is { Node: null });
            Assert(!log.ToString().Contains("partition 6 is stranded"));
            // ...but it stops counting as pending recovery, so the balancer is not paused for the
            // life of the process over content that no claim can ever reach. Partition 6 is still
            // mid-recovery from the claim above, so it has to be finished first for this to mean
            // anything - otherwise the assert would pass on 6's account and say nothing about 5.
            Assert(registry.RecoveryPending());
            // 6 is still unfinished here (its own claim lease lapsed while the clock was advanced),
            // so finish it: claim it again - the auto-pick STILL offers 6 and still never 5 - and
            // heartbeat the ownership that clears Recovering.
            RecoveryClaim[] again = registry.ClaimRecoveries(two.Node, two.Epoch, []);
            Assert(again.Length == 1 && again[0].Ownership.Partition == 6);
            registry.Heartbeat(two.Node, two.Epoch, [new(6, registry.GetPartitionOwner(6).Generation)]);
            Assert(!registry.GetPartitionOwner(6).Recovering);
            Assert(!registry.RecoveryPending());
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }
    }

    private static void TestRetirementIndex()
    {
        static PartitionVersion V(ulong partition) => new(partition, 1, 0);
        LineageRecord Rec(string id, long clusterTime, ulong[] inputs, ulong[] outputs) =>
            new(id, LineageKind.Merge, [.. inputs.Select(V)], [.. outputs.Select(V)], [], clusterTime, false);

        // 1 merged away; 2 merged away then re-emitted later; 3 only ever an output;
        // 4 and 5 tie on ClusterTime (earlier record must win); 6 never named at all.
        List<LineageRecord> lineage = [
            Rec("a", 100, [1, 2], [10]),
            Rec("b", 200, [7], [2]),
            Rec("c", 150, [8], [3]),
            Rec("d", 300, [4, 5], [11]),
            Rec("e", 300, [12], [4]),
        ];

        // The predicate exactly as it read before the index existed.
        bool Scan(ulong partition) => lineage
            .Where(x => x.Inputs.Any(y => y.Partition == partition) || x.Outputs.Any(y => y.Partition == partition))
            .OrderByDescending(x => x.ClusterTime)
            .FirstOrDefault() is { } latest
            && latest.Inputs.Any(y => y.Partition == partition)
            && latest.Outputs.All(y => y.Partition != partition);

        // The index exactly as GatewayRegistry maintains it.
        Dictionary<ulong, (long ClusterTime, bool Retired)> index = [];
        foreach (LineageRecord record in lineage)
            foreach (ulong partition in record.Inputs.Select(x => x.Partition)
                         .Concat(record.Outputs.Select(x => x.Partition)).Distinct())
            {
                if (index.TryGetValue(partition, out (long ClusterTime, bool Retired) current)
                    && current.ClusterTime >= record.ClusterTime)
                    continue;
                index[partition] = (record.ClusterTime,
                    record.Inputs.Any(y => y.Partition == partition)
                    && record.Outputs.All(y => y.Partition != partition));
            }
        bool Indexed(ulong partition) => index.TryGetValue(partition, out (long ClusterTime, bool Retired) e) && e.Retired;

        for (ulong partition = 0; partition <= 13; partition++)
            Assert(Scan(partition) == Indexed(partition));

        // And the specific claims, so a future refactor cannot satisfy the differential by
        // breaking both sides the same way.
        Assert(Indexed(1));                       // consumed at 100, never re-emitted
        Assert(!Indexed(2));                      // consumed at 100, RE-EMITTED at 200
        Assert(!Indexed(3));                      // only ever an output
        // Tie at 300 between "d" (4 as INPUT) and "e" (4 as OUTPUT). A stable
        // OrderByDescending keeps list order among equal keys, so "d" wins and 4 reads as
        // retired - and the index reproduces that by refusing to overwrite on >= rather
        // than >. This assertion was written backwards first; the differential loop above
        // caught it, which is the point of having both.
        Assert(Indexed(4));
        Assert(Indexed(5));                       // consumed at 300, never re-emitted
        Assert(!Indexed(6));                      // never named by any record
        Assert(!Indexed(13));                     // beyond every id mentioned
    }

    private static void TestSessionsAwayFromBody()
    {
        DateTimeOffset start = DateTimeOffset.UnixEpoch;
        TimeSpan grace = TimeSpan.FromSeconds(30);
        Dictionary<ulong, DateTimeOffset> since = [];
        static DirectGateway.SessionPlacement P(ulong client, string? sessionNode, ulong partition, string? owner,
            bool recovering = false, bool switching = false) => new(client, sessionNode, partition, owner, recovering, switching);

        // Session and owner agree: nothing recorded.
        var due = DirectGateway.SessionsAwayFromBody([P(10, "node-2", 1, "node-2")], since, start, grace);
        Assert(due.Count == 0 && since.Count == 0);

        // Apart, inside the grace: dated, not due.
        due = DirectGateway.SessionsAwayFromBody([P(10, "node-2", 1, "node-4")], since, start, grace);
        Assert(due.Count == 0 && since.TryGetValue(10, out DateTimeOffset dated) && dated == start);
        due = DirectGateway.SessionsAwayFromBody([P(10, "node-2", 1, "node-4")], since, start + grace - TimeSpan.FromSeconds(1), grace);
        Assert(due.Count == 0);

        // Past the grace: due once, naming both nodes and the age; the record stands until the caller clears it.
        due = DirectGateway.SessionsAwayFromBody([P(10, "node-2", 1, "node-4")], since, start + grace, grace);
        Assert(due.Count == 1 && due[0].Client == 10 && due[0].Partition == 1 && due[0].OwnerNode == "node-4"
            && due[0].SessionNode == "node-2" && due[0].Seconds >= grace.TotalSeconds);

        // Agreement again (the session moved, or the partition came back) clears the record.
        due = DirectGateway.SessionsAwayFromBody([P(10, "node-4", 1, "node-4")], since, start + grace + TimeSpan.FromSeconds(1), grace);
        Assert(due.Count == 0 && since.Count == 0);

        // The mismatch is the switch itself, the partition is unowned or still recovering, or the
        // session's backend is not a served node: none of these is "away", and each clears the record.
        since[10] = start;
        due = DirectGateway.SessionsAwayFromBody([P(10, "node-2", 1, "node-4", switching: true)], since, start + grace, grace);
        Assert(due.Count == 0 && since.Count == 0);
        since[10] = start;
        due = DirectGateway.SessionsAwayFromBody([P(10, "node-2", 1, null)], since, start + grace, grace);
        Assert(due.Count == 0 && since.Count == 0);
        since[10] = start;
        due = DirectGateway.SessionsAwayFromBody([P(10, "node-2", 1, "node-4", recovering: true)], since, start + grace, grace);
        Assert(due.Count == 0 && since.Count == 0);
        since[10] = start;
        due = DirectGateway.SessionsAwayFromBody([P(10, null, 1, "node-4")], since, start + grace, grace);
        Assert(due.Count == 0 && since.Count == 0);

        // A client that is gone is forgotten; a client that stays apart keeps its original date.
        due = DirectGateway.SessionsAwayFromBody([P(10, "node-2", 1, "node-4"), P(11, "node-2", 2, "node-5")], since, start, grace);
        Assert(due.Count == 0 && since.Count == 2);
        due = DirectGateway.SessionsAwayFromBody([P(11, "node-2", 2, "node-5")], since, start + grace, grace);
        Assert(due.Count == 1 && due[0].Client == 11 && since.Count == 1 && since.ContainsKey(11));
    }

    private static void TestRouteOverReport()
    {
        DateTimeOffset start = DateTimeOffset.UnixEpoch;
        TimeSpan grace = TimeSpan.FromSeconds(120);
        Dictionary<ulong, DateTimeOffset> since = [];

        // Reported client-bearing: the route agrees, nothing is recorded as contradicted.
        HashSet<ulong> routed = DirectGateway.RoutedClientBearing(
            [(10UL, 1UL)], [1UL], since, start, grace, out var expired);
        Assert(routed.SetEquals([1UL]) && expired.Count == 0 && since.Count == 0);

        // Contradicted, inside the grace: the route still wins, and the disagreement is dated.
        routed = DirectGateway.RoutedClientBearing(
            [(10UL, 1UL)], [], since, start, grace, out expired);
        Assert(routed.SetEquals([1UL]) && expired.Count == 0);
        Assert(since.TryGetValue(1UL, out DateTimeOffset dated) && dated == start);

        // Still inside the grace one second before it lapses.
        routed = DirectGateway.RoutedClientBearing(
            [(10UL, 1UL)], [], since, start + grace - TimeSpan.FromSeconds(1), grace, out expired);
        Assert(routed.SetEquals([1UL]) && expired.Count == 0);

        // Past the grace: the node report wins, the partition is movable, and it is
        // reported once with the client and the age of the disagreement.
        routed = DirectGateway.RoutedClientBearing(
            [(10UL, 1UL)], [], since, start + grace, grace, out expired);
        Assert(routed.Count == 0);
        Assert(expired.Count == 1 && expired[0].Client == 10UL && expired[0].Partition == 1UL
            && expired[0].Seconds >= grace.TotalSeconds);

        // A later agreeing report clears the record, so the grace restarts on the next
        // disagreement rather than staying expired forever.
        routed = DirectGateway.RoutedClientBearing(
            [(10UL, 1UL)], [1UL], since, start + grace + TimeSpan.FromSeconds(1), grace, out expired);
        Assert(routed.SetEquals([1UL]) && since.Count == 0);
        routed = DirectGateway.RoutedClientBearing(
            [(10UL, 1UL)], [], since, start + grace + TimeSpan.FromSeconds(2), grace, out expired);
        Assert(routed.SetEquals([1UL]) && expired.Count == 0);

        // A partition nothing routes to any more is forgotten, so the map cannot grow
        // without bound over a long run.
        routed = DirectGateway.RoutedClientBearing(
            [], [], since, start + grace + TimeSpan.FromSeconds(3), grace, out expired);
        Assert(routed.Count == 0 && since.Count == 0 && expired.Count == 0);

        // Two clients on one partition: one expiring entry, not two.
        routed = DirectGateway.RoutedClientBearing(
            [(10UL, 5UL), (11UL, 5UL)], [], since, start, grace, out expired);
        Assert(routed.SetEquals([5UL]) && since.Count == 1);
        routed = DirectGateway.RoutedClientBearing(
            [(10UL, 5UL), (11UL, 5UL)], [], since, start + grace, grace, out expired);
        Assert(routed.Count == 0 && expired.Count == 2 && expired.All(x => x.Partition == 5UL));
    }

    private static void AssertFrame(byte[] actual, byte[] expected, int usedBitsInLastByte)
    {
        Assert(actual.Length == expected.Length);
        Assert(actual.AsSpan(0, actual.Length - 1).SequenceEqual(expected.AsSpan(0, expected.Length - 1)));
        int mask = (1 << usedBitsInLastByte) - 1;
        Assert((actual[^1] & mask) == (expected[^1] & mask));
    }

    private static void TestLeases()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-lease-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            long recoveryGeneration;
            using (var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)))
            {
                NodeRegistration first = Register(registry, "first", "one", Guid.NewGuid());
                PartitionOwnership renewed = registry.ClaimPartition(1, first.Node, first.Epoch, 0);
                PartitionOwnership abandoned = registry.ClaimPartition(2, first.Node, first.Epoch, 0);
                long renewedGeneration = renewed.Generation;
                long abandonedGeneration = abandoned.Generation;
                Assert(registry.RouteClient(98, 2) is { Client: 98, Partition: 2, Node: "first", Endpoint: "one" });
				ClientPartition previousRoute = registry.GetClientPartition(98)!;
				Assert(registry.RouteClient(98, 1).Partition == 1);
				Assert(registry.RestoreClientRoute(98, previousRoute)?.Partition == 2);
                AssertArgument(() => registry.RouteClient(0, 2));
                AssertConflict(() => registry.RouteClient(100, 999));
                registry.RegisterSave(new SaveId(1, renewedGeneration, 1), first.Node, first.Epoch, SaveKind.Periodic, 1, "old", 1, "one/save");

                now += TimeSpan.FromSeconds(6);
                registry.Heartbeat(first.Node, first.Epoch, [new(1, renewedGeneration)], [new(42, 1, 100, renewedGeneration)]);
                Assert(registry.ResolveClientRoute(42) is { Partition: 1, Node: "first", Endpoint: "one" });
                registry.Heartbeat(first.Node, first.Epoch, [new(1, renewedGeneration)], [new(42, 0, 0, 0)]);
                Assert(registry.ResolveClientRoute(42)?.Partition == 1);
                Assert(registry.ResolveClientRoute(99) == null);
                now += TimeSpan.FromSeconds(5);
                Assert(registry.ExpireLeases() == 1);
                Assert(registry.GetPartitionOwner(1).Node == first.Node);
                Assert(registry.GetPartitionOwner(2).Node == null && registry.GetPartitionOwner(2).Generation == abandonedGeneration + 1);

                NodeRegistration second = Register(registry, "second", "two", Guid.NewGuid());
                AssertConflict(() => registry.ClaimPartition(1, second.Node, second.Epoch, renewedGeneration));
                now += TimeSpan.FromSeconds(6);
                Assert(registry.ExpireLeases() == 1);
                PartitionOwnership expired = registry.GetPartitionOwner(1);
                Assert(expired.Node == null && expired.Generation == renewedGeneration + 1);
                // T-0197: UNOWNED AND BEING RE-HOMED IS TRANSIENT. The lease expired a moment ago
                // and a recovery is what happens next, so the client must be told to come back and
                // not refused outright - this assertion is the classification the gateway holds on.
                AssertBusy(() => registry.ResolveClientRoute(42));
                Assert(registry.ExpireLeases() == 0 && registry.GetPartitionOwner(1).Generation == expired.Generation);
                AssertConflict(() => registry.Heartbeat(first.Node, first.Epoch, []));
                AssertConflict(() => registry.RegisterSave(new SaveId(1, renewedGeneration, 2), first.Node, first.Epoch, SaveKind.Periodic, 1, "stale", 2, "one/stale"));

                RecoveryClaim recovery = registry.ClaimRecoveries(second.Node, second.Epoch, []).Single();
                recoveryGeneration = recovery.Ownership.Generation;
                Assert(recovery.ResolveCover.Single().Save.SaveId == new SaveId(1, renewedGeneration, 1));
                // Mid-recovery: the partition is claimed but not yet reported, which is the exact
                // window T-0197 measured a player being locked out of for the rest of the run.
                AssertBusy(() => registry.ResolveClientRoute(42));
                registry.Heartbeat(second.Node, second.Epoch, [new(1, recoveryGeneration)]);
                Assert(registry.ResolveClientRoute(42) is { Partition: 1, Node: "second", Endpoint: "two" });
                NodeRegistration closed = registry.CloseNode(second.Node, second.Epoch, TimeSpan.FromSeconds(5));
                Assert(closed.State == NodeState.Closed && closed.ForceDrainAt == now + TimeSpan.FromSeconds(5));
                AssertConflict(() => registry.ClaimPartition(3, second.Node, second.Epoch, 0));
                NodeRegistration third = Register(registry, "third", "three", Guid.NewGuid());
                HandoverRecord draining = registry.Begin("closed-source", 1, second.Node, second.Epoch, recoveryGeneration, third.Node, third.Epoch);
                Assert(draining.State == HandoverState.Begun);
                registry.Abort(draining.Id);
                AssertConflict(() => registry.MarkEmpty(second.Node, second.Epoch));
                registry.RegisterSave(new SaveId(1, recoveryGeneration, 1), second.Node, second.Epoch, SaveKind.Periodic, 1, "periodic", 3, "two/periodic");
                registry.RegisterSave(new SaveId(1, recoveryGeneration, 2), second.Node, second.Epoch, SaveKind.Final, 1, "final", 4, "two/final");
                PartitionOwnership released = registry.ReleasePartition(1, second.Node, second.Epoch, recoveryGeneration);
                Assert(released.Node == null && released.Generation == recoveryGeneration + 1);
                Assert(registry.ReleasePartition(1, second.Node, second.Epoch, recoveryGeneration) == released);
                Assert(registry.MarkEmpty(second.Node, second.Epoch).State == NodeState.Empty);
                registry.Checkpoint();
            }
            using (var restarted = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)))
            {
                Assert(restarted.GetPartitionOwner(1).Node == null && restarted.GetPartitionOwner(1).Generation == recoveryGeneration + 1);
                Assert(restarted.NewestSave(1)?.Kind == SaveKind.Final);
                AssertConflict(() => restarted.ClaimPartition(3, "second", 1, 0));
                Assert(restarted.ResolveClientRoute(42) == null);
                Assert(restarted.Nodes().Single(x => x.Node == "second") is { State: NodeState.Empty, ForceDrainAt: not null });
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    // A rejoin has to land on the node holding the player's body. The live client route is soft
    // (Gateway §6.3) and does not survive a restart, so the character home is what answers the join
    // after one - and it has to fail soft, not throw, when the body's partition is not loadable.
    private static void TestCharacterHomes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-home-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using (var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)))
            {
                NodeRegistration node = Register(registry, "first", "one", Guid.NewGuid());
                PartitionOwnership owner = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
                Assert(registry.ResolveClientRoute(42) == null);
                AssertArgument(() => ReportCharacterHome(registry, 42, 7, 0, 1, "first"));
                AssertArgument(() => ReportCharacterHome(registry, 42, 7, 900, 0, "first"));
                ReportCharacterHome(registry, 42, 7, 900, 1, "first");
                Assert(registry.GetCharacterHome(42) is { Identity: 7, Entity: 900, Partition: 1, Node: "first" });
                // Never routed, never heartbeaten: the body's partition alone answers the join.
                Assert(registry.RoutedPartition(42) == null);
                Assert(registry.ResolveClientRoute(42) is { Partition: 1, Node: "first", Endpoint: "one" });
                // T-0234: the offline/release guards see where the joining client is headed before any node
                // reports it, without making it a live placement (the fail-soft join below still holds).
                Assert(registry.RoutedPartition(42) == 1 && registry.GetClientPartition(42) == null);
                now += TimeSpan.FromSeconds(121);
                Assert(registry.RoutedPartition(42) == null);
                now -= TimeSpan.FromSeconds(121);
                registry.Heartbeat(node.Node, node.Epoch, [new(1, owner.Generation)]);
                registry.RegisterSave(new SaveId(1, owner.Generation, 1), node.Node, node.Epoch, SaveKind.Final, 1, "drain", 1, "one/final");
                registry.Checkpoint();
            }
            using (var restarted = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)))
            {
                Assert(restarted.GetCharacterHome(42) is { Entity: 900, Partition: 1 });
                Assert(restarted.ResolveClientRoute(42) is { Partition: 1, Node: "first" });
                Assert(!restarted.PendingHomeRecovery(42));   // owned: the route answers, no hold
                now += TimeSpan.FromSeconds(11);
                Assert(restarted.ExpireLeases() == 1);
                // The home outlives the node that reported it; with nowhere to send the client the
                // join falls back to the default backend rather than failing.
                Assert(restarted.GetCharacterHome(42)?.Entity == 900 && restarted.ResolveClientRoute(42) == null);
                // The §6.4 join hold: unowned but recoverable is the drain window - the join defers
                // so the retry lands on the recovered body instead of forking a fresh one on the
                // default backend. A client with no recorded home has nothing to wait for.
                Assert(restarted.PendingHomeRecovery(42));
                Assert(!restarted.PendingHomeRecovery(43));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    // w29 2026-09-07 pilot-10: a seated body reports no home, the pre-boarding partition it last
    // reported was merged away, and the rejoin after the restart resolved the retired id, fell
    // back to the default backend and forked a second body. A lineage must carry the home with
    // it, and a home that still names a retired id (persisted before this fix) must follow the
    // lineage at join time instead of failing soft into a fresh spawn.
    /// <summary>
    /// T-0237: a join must not be routed to the SOURCE of a partition that is being handed over (w33 2026-09-27
    /// 20:12:11: Pilot-038's home 111231 was co-located node-2 -> node-1 while its join was answered; it was routed
    /// to node-2, got a fresh body, and node-1 despawned the seated one). A home mid-handover holds the join like a
    /// recovering one, a live placement mid-handover resolves busy, and both answer again once it is over.
    /// </summary>
    /// <summary>T-0269: a wake loads on the demander's node unless that node is above ratio x the mean load.</summary>
    private static void TestOfflineWakePlacement()
    {
        static void Assert(bool value, string what) { if (!value) throw new InvalidOperationException("offline-wake-placement: " + what); }
        // node-1 at 23:44 (w33): 2.31M sim-us/s against 0.11-0.18M.
        var overloaded = new Dictionary<string, double> { ["node-1"] = 2_312_395, ["node-2"] = 105_697, ["node-3"] = 129_903, ["node-4"] = 176_220 };
        Assert(DirectGateway.OfflineWakeTarget("node-1", "approach", overloaded, 1.5) == "node-2", "an overloaded demander's approach wake goes to the least-loaded node");
        Assert(DirectGateway.OfflineWakeTarget("node-1", "merge", overloaded, 1.5) == "node-2", "so does a merge wake");
        Assert(DirectGateway.OfflineWakeTarget("node-1", "overlap", overloaded, 1.5) == "node-1", "an overlap stays with the demander");
        Assert(DirectGateway.OfflineWakeTarget("node-3", "approach", overloaded, 1.5) == "node-3", "a demander below the ratio keeps its wake");
        Assert(DirectGateway.OfflineWakeTarget("node-1", "approach", overloaded, 0) == "node-1", "ratio 0 turns the rule off");
        var even = new Dictionary<string, double> { ["node-1"] = 500_000, ["node-2"] = 480_000, ["node-3"] = 520_000 };
        Assert(DirectGateway.OfflineWakeTarget("node-3", "approach", even, 1.5) == "node-3", "a balanced cluster keeps every wake with its demander");
        Assert(DirectGateway.OfflineWakeTarget("node-1", "approach", new Dictionary<string, double> { ["node-1"] = 9 }, 1.5) == "node-1", "a single node keeps it");
    }

    /// <summary>
    /// T-0268: POST /balance/migrate plans the balancer's own unit - the named partition's envelope component
    /// on its node - to the named target, or to the least-loaded other node, and refuses a held member.
    /// </summary>
    // T-0391: the live override of a balancer knob changes exactly the named field and refuses what it cannot apply.
    private static void TestCostModelOverrides()
    {
        static void Assert(bool value, string what) { if (!value) throw new InvalidOperationException("cost-model-overrides: " + what); }
        static bool Throws(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } catch (FormatException) { return true; } }
        var model = new CostModel();
        Assert(model.ClientBoundMaxMembers == 0, "default ClientBoundMaxMembers is 0 (T-0391), got " + model.ClientBoundMaxMembers);
        CostModel off = CostModel.WithOverrides(model, new[] { KeyValuePair.Create("clientboundmaxmembers", "30") });
        Assert(off.ClientBoundMaxMembers == 30, "override to 30 applied, got " + off.ClientBoundMaxMembers);
        Assert(off with { ClientBoundMaxMembers = model.ClientBoundMaxMembers } == model, "no other field changed");
        CostModel tol = CostModel.WithOverrides(model, new[] { KeyValuePair.Create("BalanceRiskTolerance", "0.4") });
        Assert(Math.Abs(tol.BalanceRiskTolerance - 0.4) < 1e-12, "double field applied, got " + tol.BalanceRiskTolerance);
        Assert(Throws(() => CostModel.WithOverrides(model, new[] { KeyValuePair.Create("NoSuchKnob", "1") })), "unknown field refused");
        Assert(Throws(() => CostModel.WithOverrides(model, new[] { KeyValuePair.Create("ClientBoundMaxMembers", "x") })), "unparsable value refused");
        Assert(model.ClientBoundMaxMembers == 0, "the input model is untouched");
        // T-0393: every pace knob is live-tunable the same way, and each override changes only its own field.
        foreach ((string field, string value, Func<CostModel, double> read, Func<CostModel, CostModel> reset) in new (string, string, Func<CostModel, double>, Func<CostModel, CostModel>)[]
        {
            ("HitchMsPerMove", "1000", m => m.HitchMsPerMove, m => m with { HitchMsPerMove = model.HitchMsPerMove }),
            ("minGainFraction", "0.02", m => m.MinGainFraction, m => m with { MinGainFraction = model.MinGainFraction }),
            ("BalanceLowWater", "0.15", m => m.BalanceLowWater, m => m with { BalanceLowWater = model.BalanceLowWater }),
            ("ResidenceMinFreezeMs", "0", m => m.ResidenceMinFreezeMs, m => m with { ResidenceMinFreezeMs = model.ResidenceMinFreezeMs }),
            ("BudgetMovesPerNodeHour", "6", m => m.BudgetMovesPerNodeHour, m => m with { BudgetMovesPerNodeHour = model.BudgetMovesPerNodeHour }),
            ("BudgetUnderDeviation", "0.6", m => m.BudgetUnderDeviation, m => m with { BudgetUnderDeviation = model.BudgetUnderDeviation }),
        })
        {
            CostModel set = CostModel.WithOverrides(model, new[] { KeyValuePair.Create(field, value) });
            Assert(read(set) == double.Parse(value, System.Globalization.CultureInfo.InvariantCulture), $"{field} override to {value} applied, got {read(set)}");
            Assert(reset(set) == model, $"{field}: no other field changed");
        }
        Assert(CostModel.WithOverrides(model, new[] { KeyValuePair.Create("BalanceLowWater", "0.15") }).EffectiveLowWater() == 0.15, "low water 0.15 is the active gate");
        Assert(Throws(() => CostModel.WithOverrides(model, new[] { KeyValuePair.Create("EffectiveLowWater", "0.1") })),
            "EffectiveLowWater is derived, not a field an override could silently fail to set");
    }

    private static void TestBalanceTestTrigger()
    {
        static PartitionInfo At(ulong id, double x, int pcu, ulong[]? clients = null) =>
            new(id, 1, true, new PartitionSphere(x, 0, 0, 100), 0, clients ?? [], pcu, 400L * pcu + 10_000);
        static PartitionReport Report(PartitionInfo[] partitions) => new(3000, 1000, partitions, 0, 50);
        (PartitionReport, string)[] nodes =
        [
            (Report([At(1, 0, 500, [77]), At(2, 2000, 300), At(3, 900_000, 100)]), "node-1"),
            (Report([At(4, 500_000, 900)]), "node-2"),
            (Report([At(5, 700_000, 50)]), "node-3"),
        ];
        static void Assert(bool value, string what) { if (!value) throw new InvalidOperationException("balance-test-trigger: " + what); }
        HashSet<ulong> none = [];
        DirectGateway.TestMigration named = DirectGateway.PlanTestMigration(nodes, 1, "node-2", new CostModel(), none, _ => false);
        Assert(named.Refusal == null && named.Plan is { Source: "node-1", Target: "node-2", Kind: DirectGateway.RebalanceKind.Client } plan
            && plan.Members.Select(m => m.Id).Order().SequenceEqual([1UL, 2UL]) && named.Clients == 1,
            "the named partition moves with its envelope component, not alone and not the far partition 3");
        DirectGateway.TestMigration least = DirectGateway.PlanTestMigration(nodes, 3, null, new CostModel(), none, _ => false);
        Assert(least.Plan is { Target: "node-3", Kind: DirectGateway.RebalanceKind.Load } && least.Plan.Value.Members.Length == 1,
            "no target: the least-loaded other node");
        Assert(DirectGateway.PlanTestMigration(nodes, 1, "node-2", new CostModel(), none, id => id == 2) is { Plan: null, Refusal: not null },
            "a held member refuses the whole component");
        Assert(DirectGateway.PlanTestMigration(nodes, 1, "node-1", new CostModel(), none, _ => false).Refusal != null
            && DirectGateway.PlanTestMigration(nodes, 1, "node-9", new CostModel(), none, _ => false).Refusal != null
            && DirectGateway.PlanTestMigration(nodes, 42, null, new CostModel(), none, _ => false).Refusal != null,
            "own node, unknown node and unknown partition are refusals");
    }

    private static void TestBalanceSimulationSpeed()
    {
        static PartitionInfo Part(ulong id, double x, long cost) =>
            new(id, 1, true, new PartitionSphere(x, 0, 0, 100), 0, [], 1000, 410_000, cost);
        static PartitionReport Report(double speed, params PartitionInfo[] parts) =>
            new(3000, 1000, parts, 0, 50, speed, true);
        static void Check(bool condition, string why)
        { if (!condition) throw new InvalidOperationException("balance-simulation-speed: " + why); }

        PartitionReport lowSum = Report(1, Part(1, 0, 10_000));
        Check(DirectGateway.EffectiveNodeLoad(lowSum, true) == 10_000, "normal speed keeps measured sum");
        Check(DirectGateway.EffectiveNodeLoad(lowSum with { SimulationRatio = 0.98 }, true) == 10_000,
            "0.98 boundary keeps measured sum");
        // T-0355: an empty (dormant, 4 Hz) node is not the busiest node of the cluster.
        Check(DirectGateway.EffectiveNodeLoad(Report(0.07), true) == 0, "an empty node at a dormant ratio has no load");
        Check(DirectGateway.EffectiveNodeLoad(Report(0.5), true) == 0, "an empty node has no load whatever its ratio");
        Check(DirectGateway.EffectiveNodeLoad(lowSum with { SimulationRatio = 0.979 }, true) > 1_000_000,
            "below boundary uses reciprocal");
        Check(DirectGateway.EffectiveNodeLoad(lowSum with { SimulationRatio = 0.5 }, true) == 2_000_000,
            "0.5 gives 2000 ms/s");
        Check(DirectGateway.EffectiveNodeLoad(lowSum with { SimulationRatio = 0.25 }, true) == 4_000_000,
            "0.25 gives 4000 ms/s");
        foreach (double invalid in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Check(DirectGateway.EffectiveNodeLoad(lowSum with { SimulationRatio = invalid }, true) == 10_000,
                "invalid speed keeps measured sum");
        Check(DirectGateway.EffectiveNodeLoad(lowSum with { SimulationRatio = 0.25 }, false) == 1000,
            "PCU pass never mixes elapsed time");

        (PartitionReport Report, string Node)[] nodes =
        [
            (Report(1, Part(2, 0, 1_000_000), Part(3, 10_000, 500_000)), "source"),
            (Report(0.5, Part(4, 100_000, 10_000)), "slow"),
            (Report(1, Part(5, 200_000, 200_000)), "healthy"),
        ];
        DirectGateway.RebalancePlan[] plans = DirectGateway.ChooseRebalancePlans(nodes, new CostModel(), [],
            _ => false, fullPlan: true);
        Check(plans.Any(p => p.Source == "source" && p.Target == "healthy")
            && plans.All(p => p.Target != "slow"), "placement avoids low-sum slow destination");
        DirectGateway.TestMigration selected = DirectGateway.PlanTestMigration(nodes, 2, null,
            new CostModel(), [], _ => false);
        Check(selected.Plan is { Target: "healthy" }, "automatic target choice avoids slow node");
        (PartitionReport Report, string Node)[] normal =
        [nodes[0], (nodes[1].Report with { SimulationRatio = 1 }, "slow"), nodes[2]];
        Check(DirectGateway.EffectiveNodeLoad(normal[1].Report, true) == 10_000
            && DirectGateway.PlanTestMigration(normal, 2, null, new CostModel(), [], _ => false)
                .Plan is { Target: "slow" }, "normal-speed target keeps measured ordering");
    }

    private static void TestJoinWaitsForMovingHome()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-moving-home-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration first = Register(registry, "first", "one", Guid.NewGuid());
            NodeRegistration second = Register(registry, "second", "two", Guid.NewGuid());
            PartitionOwnership owner = registry.ClaimPartition(1, first.Node, first.Epoch, 0);
            ReportCharacterHome(registry, 42, 7, 900, 1, "first");
            Assert(registry.RouteClient(43, 1) is { Partition: 1, Node: "first" });
            Assert(!registry.PendingHomeRecovery(42) && registry.ResolveClientRoute(42) is { Partition: 1, Node: "first" });
            HandoverRecord moving = registry.Begin("co-location", 1, first.Node, first.Epoch, owner.Generation, second.Node, second.Epoch);
            Assert(registry.PendingHomeRecovery(42));                 // the join is held, not routed to the source
            AssertBusy(() => registry.ResolveClientRoute(43));          // a live placement is transient, not wrong
            registry.Abort(moving.Id);
            Assert(!registry.PendingHomeRecovery(42) && registry.ResolveClientRoute(42) is { Partition: 1, Node: "first" });
            Assert(registry.ResolveClientRoute(43) is { Partition: 1, Node: "first" });
        }
        finally { Directory.Delete(directory, true); }
    }

    private static long ReportCharacterHome(GatewayRegistry registry, ulong client, long identity,
        long entity, ulong partition, string node)
    {
        NodeRegistration writer = registry.Nodes().Single(x => x.Node == node);
        long generation = registry.Partitions().SingleOrDefault(x => x.Partition == partition)?.Generation ?? 0;
        return registry.SetCharacterHome(client, identity, entity, partition, node, writer.Epoch,
            generation, registry.GetCharacterHome(client)?.Version ?? 0, registry.GetCharacterHome(client)?.WriteRevision ?? 0);
    }

    private static void SeedLegacyCharacterHome(GatewayRegistry registry, ulong client, CharacterHome home)
    {
        // The lineage migration test needs a home written BEFORE retirements were repointed.
        // Seed precisely that legacy row; a new report against a retired claim must now fail.
        object state = typeof(GatewayRegistry).GetField("_state", System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance)!.GetValue(registry)!;
        var homes = (Dictionary<ulong, CharacterHome>)state.GetType().GetProperty("CharacterHomes")!.GetValue(state)!;
        homes[client] = home;
    }

    private static void TestCharacterHomeWriterFence()
    {
        string directory = Directory.CreateTempSubdirectory("home-writer-fence-").FullName;
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration first = Register(registry, "first", "one", Guid.NewGuid());
            NodeRegistration second = Register(registry, "second", "two", Guid.NewGuid());
            PartitionOwnership old = registry.ClaimPartition(1, first.Node, first.Epoch, 0);
            PartitionOwnership current = registry.ClaimPartition(2, second.Node, second.Epoch, 0);
            ReportCharacterHome(registry, 42, 7, 900, 1, first.Node);
            // The HTTP handler decoded A's report before its ownership changed. Hold it here,
            // accept B's valid report, then let the delayed handler reach the Registry lock.
            long oldVersion = registry.GetCharacterHome(42)!.Version;
            long oldRevision = registry.GetCharacterHome(42)!.WriteRevision;
            CharacterHome? expected = null;
            using var decoded = new System.Threading.ManualResetEventSlim();
            using var resume = new System.Threading.ManualResetEventSlim();
            Task<bool> delayed = Task.Run(() =>
            {
                decoded.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("home report seam");
                try { registry.SetCharacterHome(42, 7, 900, 1, first.Node, first.Epoch, old.Generation, oldVersion, oldRevision); return false; }
                catch (RegistryConflictException) { return true; }
            });
            try
            {
                Assert(decoded.Wait(TimeSpan.FromSeconds(5)));
                registry.ReleasePartition(1, first.Node, first.Epoch, old.Generation);
                registry.ClaimPartition(1, second.Node, second.Epoch, 0);
                ReportCharacterHome(registry, 42, 7, 901, 2, second.Node);
                expected = registry.GetCharacterHome(42);
            }
            finally { resume.Set(); }
            bool fenced = delayed.GetAwaiter().GetResult();
            if (!fenced || registry.GetCharacterHome(42) != expected)
                throw new InvalidOperationException("character-home-writer-fence: delayed former owner overwrote current home");
            // Both partitions can remain owned when the character moves. The older captured
            // report must still lose to the already-recorded home, without changing its version.
            PartitionOwnership another = registry.ClaimPartition(3, second.Node, second.Epoch, 0);
            long capturedVersion = expected!.Version;
            ReportCharacterHome(registry, 42, 7, 902, 3, second.Node);
            CharacterHome latest = registry.GetCharacterHome(42)!;
            AssertConflict(() => registry.SetCharacterHome(42, 7, 901, 2, second.Node,
                second.Epoch, current.Generation, capturedVersion, latest.WriteRevision));
            Assert(registry.GetCharacterHome(42) == latest);
            AssertConflict(() => registry.SetCharacterHome(42, 7, 902, 3, second.Node,
                second.Epoch + 1, another.Generation, latest.Version, latest.WriteRevision));
            AssertConflict(() => registry.SetCharacterHome(42, 7, 902, 3, second.Node,
                second.Epoch, another.Generation + 1, latest.Version, latest.WriteRevision));
            now += TimeSpan.FromSeconds(10);
            AssertConflict(() => registry.SetCharacterHome(42, 7, 902, 3, second.Node,
                second.Epoch, another.Generation, latest.Version, latest.WriteRevision)); // unchanged is also fenced
            Assert(registry.GetCharacterHome(42) == latest); // durable read survives expiry
            registry.Heartbeat(second.Node, second.Epoch, [new(2, current.Generation), new(3, another.Generation)]);
            registry.CloseNode(second.Node, second.Epoch, TimeSpan.FromMinutes(1));
            Assert(ReportCharacterHome(registry, 42, 7, 902, 3, second.Node) == latest.Version);
            registry.RecordLineage("home-retirement", LineageKind.Merge,
                [new(2, current.Generation), new(3, another.Generation)], [new(3, another.Generation)], null, 10);
            AssertConflict(() => registry.SetCharacterHome(43, 8, 903, 2, second.Node,
                second.Epoch, current.Generation, 0, 0));
            NodeRegistration third = Register(registry, "third", "three", Guid.NewGuid());
            now += TimeSpan.FromSeconds(9);
            PartitionOwnership fresh = registry.ClaimPartition(9, third.Node, third.Epoch, 0);
            now += TimeSpan.FromSeconds(2);
            Assert(fresh.LeaseExpires > now);
            AssertConflict(() => registry.SetCharacterHome(49, 9, 909, 9, third.Node,
                third.Epoch, fresh.Generation, 0, 0)); // node lease expired, partition lease live

        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestCharacterReapPermits()
    {
        string directory = Directory.CreateTempSubdirectory("character-reap-").FullName;
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using (var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)))
            {
                NodeRegistration one = Register(registry, "one", "one", Guid.NewGuid());
                NodeRegistration two = Register(registry, "two", "two", Guid.NewGuid());
                PartitionOwnership x = registry.ClaimPartition(1, one.Node, one.Epoch, 0);
                PartitionOwnership y = registry.ClaimPartition(2, two.Node, two.Epoch, 0);
                ReportCharacterHome(registry, 42, 7, 901, 2, two.Node);
                CharacterHome lookup = registry.GetCharacterHome(42)!;
                // Hold the worker's old home answer while a fresh game frame selects X.
                // Control executes the old lookup -> queued-close policy; production uses grant.
                using var observed = new System.Threading.ManualResetEventSlim();
                using var resume = new System.Threading.ManualResetEventSlim();
                int closed = 0;
                Task worker = Task.Run(() =>
                {
                    observed.Set();
                    if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("character reap seam");
                    if (Environment.GetEnvironmentVariable("CLUSTER_TEST_CHARACTER_REAP_LEGACY") == "1")
                    {
                        if (lookup.Entity != 900) closed++; // original asynchronous stale-home decision
                    }
                    else
                    {
                        try
                        {
                            registry.AuthorizeCharacterReap(42, 7, 900, one.Node, one.Epoch, 1, x.Generation,
                                lookup.Version, lookup.WriteRevision);
                            closed++;
                        }
                        catch (RegistryConflictException) { }
                    }
                });
                try
                {
                    Assert(observed.Wait(TimeSpan.FromSeconds(5)));
                    ReportCharacterHome(registry, 42, 7, 900, 1, one.Node);
                }
                finally { resume.Set(); }
                worker.GetAwaiter().GetResult();
                if (closed != 0) throw new InvalidOperationException("character-reap-permits: old home answer authorized closing the new current body");
                Assert(registry.GetCharacterHome(42)!.Entity == 900);

                ReportCharacterHome(registry, 42, 7, 901, 2, two.Node);
                CharacterHome home = registry.GetCharacterHome(42)!;
                var grant = registry.AuthorizeCharacterReap(42, 7, 900, one.Node, one.Epoch, 1, x.Generation,
                    home.Version, home.WriteRevision);
                Assert(grant == registry.AuthorizeCharacterReap(42, 7, 900, one.Node, one.Epoch, 1, x.Generation,
                    home.Version, home.WriteRevision)); // response-loss replay
                Assert(registry.GetCharacterHome(42)!.Version == home.Version); // recovery stamp unchanged
                long pendingRevision = registry.GetCharacterHome(42)!.WriteRevision;
                AssertConflict(() => registry.SetCharacterHome(42, 7, 900, 1, one.Node, one.Epoch,
                    x.Generation, home.Version, pendingRevision)); // current candidate blocked even with fresh token
                registry.ResolveCharacterReap(42, grant);
                CharacterHome resolved = registry.GetCharacterHome(42)!;
                registry.ResolveCharacterReap(42, grant);
                Assert(registry.GetCharacterHome(42) == resolved); // acknowledgement idempotent
                AssertConflict(() => registry.SetCharacterHome(42, 7, 900, 1, one.Node, one.Epoch,
                    x.Generation, home.Version, pendingRevision)); // delayed during-permit request, permit now absent
                ReportCharacterHome(registry, 42, 7, 900, 1, one.Node); // fresh capture after refusal works

                // A delayed pending response after cancellation must not produce another close.
                var queue = new ClusterProtocol.CharacterReapQueue();
                Assert(queue.Enqueue(42, grant));
                Assert(!queue.FinishLookup()); // producer queued after the previous frame's drain
                Assert(queue.TryTake(out var consumed) && consumed.Consumed);
                Assert(queue.Unresolved().Length == 0); // consume is not permission to ACK before close finishes
                consumed.FinishExecution();
                queue.Resolved(consumed);
                Assert(!queue.Enqueue(42, grant));
                var newer = grant with { Operation = Guid.NewGuid(), Revision = grant.Revision + 2 };
                Assert(queue.Enqueue(42, newer));
                Assert(queue.TryTake(out var newerWork));
                queue.Resolved(consumed); // old ACK cannot remove a newer local work item
                Assert(queue.Contains(42));
                newerWork.FinishExecution();
                queue.Resolved(newerWork);
                Assert(!queue.Enqueue(42, grant)); // old grant after a newer operation
                Assert(queue.FinishLookup()); // all producer responses drained: release resolved watermarks

                // Pending permission survives Registry restart, then transfers to the actual new owner.
                ReportCharacterHome(registry, 42, 7, 901, 2, two.Node);
                home = registry.GetCharacterHome(42)!;
                grant = registry.AuthorizeCharacterReap(42, 7, 900, one.Node, one.Epoch, 1, x.Generation,
                    home.Version, home.WriteRevision);
                registry.RegisterSave(new SaveId(1, x.Generation, 1), one.Node, one.Epoch, SaveKind.Final,
                    1, "reap", 1, "one/reap", "http://one/control");
                registry.ReleasePartition(1, one.Node, one.Epoch, x.Generation);
                PartitionOwnership moved = registry.ClaimPartition(1, two.Node, two.Epoch, null);
                Assert(registry.PendingCharacterReaps(two.Node, two.Epoch).Length == 1);
                home = registry.GetCharacterHome(42)!;
                var takeover = registry.AuthorizeCharacterReap(42, 7, 900, two.Node, two.Epoch, 1, moved.Generation,
                    home.Version, home.WriteRevision);
                Assert(takeover.Operation == grant.Operation && takeover.Revision > grant.Revision);
                AssertConflict(() => registry.ResolveCharacterReap(42, grant));
                Assert(registry.GetCharacterHome(42)!.PendingReap == takeover);
                PartitionOwnership output = registry.ClaimPartition(3, two.Node, two.Epoch, 0);
                registry.RecordLineage("reap-candidate-merge", LineageKind.Merge,
                    [new(1, moved.Generation), new(3, output.Generation)], [new(3, output.Generation)], null, 10);
                string pending = System.Text.Json.JsonSerializer.Serialize(registry.PendingCharacterReaps(two.Node, two.Epoch));
                Assert(pending.Contains("\"partition\":3"));
                home = registry.GetCharacterHome(42)!;
                var topologyTakeover = registry.AuthorizeCharacterReap(42, 7, 900, two.Node, two.Epoch, 3, output.Generation,
                    home.Version, home.WriteRevision);
                AssertConflict(() => registry.ResolveCharacterReap(42, takeover));
                Assert(topologyTakeover.Partition == 3 && topologyTakeover.Revision > takeover.Revision);
                registry.RecordLineage("reap-seated-split", LineageKind.Split, [new(3, output.Generation)],
                    [new(10, 1), new(11, 1)], new Dictionary<ulong, long[]> { [10] = [990], [11] = [991, 992, 993] }, 20,
                    new Dictionary<ulong, long[]> { [10] = [900], [11] = [] });
                Assert(registry.GetCharacterHome(42)!.PendingReapPartition == 10);
                PartitionOwnership seatedOutput = registry.ClaimPartition(10, two.Node, two.Epoch, 0);
                home = registry.GetCharacterHome(42)!;
                var seatedGrant = registry.AuthorizeCharacterReap(42, 7, 900, two.Node, two.Epoch, 10, seatedOutput.Generation,
                    home.Version, home.WriteRevision);
                AssertConflict(() => registry.ResolveCharacterReap(42, topologyTakeover));
                Assert(seatedGrant.Partition == 10);

            }
            using (var restarted = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)))
            {
                CharacterHome persisted = restarted.GetCharacterHome(42)!;
                Assert(persisted.PendingReap != null && persisted.Entity == 901);
                NodeRegistration replacement = Register(restarted, "two", "replacement", Guid.NewGuid());
                Assert(persisted.PendingReapPartition == 10);
                PartitionOwnership claim = restarted.ClaimPartition(10, replacement.Node, replacement.Epoch, null);
                var replacementGrant = restarted.AuthorizeCharacterReap(42, 7, 900, replacement.Node, replacement.Epoch,
                    10, claim.Generation, persisted.Version, persisted.WriteRevision);
                Assert(replacementGrant.Epoch != persisted.PendingReap!.Epoch);
                AssertConflict(() => restarted.ResolveCharacterReap(42, persisted.PendingReap!));
                restarted.ResolveCharacterReap(42, replacementGrant);
                Assert(restarted.GetCharacterHome(42)!.PendingReap == null);
                Assert(restarted.GetCharacterHome(42)!.WriteRevision > persisted.WriteRevision);
                Assert(restarted.GetCharacterHome(42)!.Version == persisted.Version);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static void AllocateAndAbandon(string directory)
    {
        // Intentionally no using/Dispose: emulate a process loss after the allocation replies.
        var registry = new GatewayRegistry(directory, Admission);
        NodeRegistration node = Register(registry, "issued-before-crash", "one", Guid.NewGuid());
        NetworkIdBlock block = registry.AllocateNetworkIdBlock(node.Node, node.Epoch);
        EntityIdRange range = registry.AllocateEntityIdRange(node.Node, node.Epoch);
        File.WriteAllText(Path.Combine(directory, "issued.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Block = block, Range = range }));
        Environment.Exit(0);
    }

    private static void TestAllocationCrashDurability()
    {
        string directory = Directory.CreateTempSubdirectory("allocation-crash-").FullName;
        try
        {
            using (var baseline = new GatewayRegistry(directory, Admission)) baseline.Checkpoint();
            var start = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            start.ArgumentList.Add(typeof(SelfTest).Assembly.Location);
            start.ArgumentList.Add("--self-test-abandon-allocation");
            start.ArgumentList.Add(directory);
            start.Environment["CLUSTER_PERSIST_INTERVAL_SECONDS"] = "60";
            using var child = System.Diagnostics.Process.Start(start)!;
            if (!child.WaitForExit(10000))
            {
                child.Kill(entireProcessTree: true);
                throw new TimeoutException("Allocation crash fixture child");
            }
            if (child.ExitCode != 0) throw new InvalidOperationException("Allocation child failed: " + child.StandardError.ReadToEnd());
            using var issued = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "issued.json")));
            uint blockEnd = issued.RootElement.GetProperty("Block").GetProperty("End").GetUInt32();
            uint rangeIndex = issued.RootElement.GetProperty("Range").GetProperty("Index").GetUInt32();
            using var restarted = new GatewayRegistry(directory, Admission);
            NodeRegistration next = Register(restarted, "allocated-after-crash", "two", Guid.NewGuid());
            NetworkIdBlock nextBlock = restarted.AllocateNetworkIdBlock(next.Node, next.Epoch);
            EntityIdRange nextRange = restarted.AllocateEntityIdRange(next.Node, next.Epoch);
            if (nextBlock.Start <= blockEnd || nextRange.Index <= rangeIndex)
                throw new InvalidOperationException($"allocation-crash-durability: issued ranges reused: networkEnd={blockEnd}"
                    + $" nextStart={nextBlock.Start}, entityIndex={rangeIndex} nextIndex={nextRange.Index}");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static void AwaitSigterm(string directory, bool locked)
    {
        var registry = new GatewayRegistry(directory, Admission);
        Register(registry, "registered-before-sigterm", "one", Guid.NewGuid());
        if (locked)
        {
            ManualResetEventSlim taken = new();
            new Thread(() => { lock (registry.LockForSelfTest) { taken.Set(); Thread.Sleep(Timeout.Infinite); } }) { IsBackground = true }.Start();
            taken.Wait();
        }
        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, _ => DirectGateway.ExitOnSigterm(registry));
        File.WriteAllText(Path.Combine(directory, "ready"), "");
        Thread.Sleep(Timeout.Infinite);
    }

    private static void TestSigtermFlush()
    {
        foreach (bool locked in new[] { false, true })
        {
            string directory = Directory.CreateTempSubdirectory("sigterm-flush-").FullName;
            try
            {
                using (var baseline = new GatewayRegistry(directory, Admission)) baseline.Checkpoint();
                var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false };
                start.ArgumentList.Add(typeof(SelfTest).Assembly.Location);
                start.ArgumentList.Add("--self-test-sigterm");
                start.ArgumentList.Add(directory);
                start.ArgumentList.Add(locked ? "locked" : "free");
                start.Environment["CLUSTER_PERSIST_INTERVAL_SECONDS"] = "3600";
                using var child = System.Diagnostics.Process.Start(start)!;
                try
                {
                    string ready = Path.Combine(directory, "ready");
                    for (int waited = 0; !File.Exists(ready); waited += 50)
                    {
                        if (waited > 30000 || child.HasExited) throw new TimeoutException("SIGTERM fixture child did not get ready");
                        Thread.Sleep(50);
                    }
                    System.Diagnostics.Process.Start("kill", ["-TERM", child.Id.ToString()])!.WaitForExit();
                    // The 5 s budget, and a held lock must not stretch it to the supervisor's SIGKILL.
                    if (!child.WaitForExit(10000)) throw new TimeoutException($"SIGTERM hung the process, locked={locked}");
                    Assert(child.ExitCode == 143);
                }
                finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                using var restarted = new GatewayRegistry(directory, Admission);
                // Held by another thread nothing can be committed, and the exit doesn't wait for it.
                Assert(restarted.Nodes().Any(node => node.Node == "registered-before-sigterm") != locked);
            }
            finally { Directory.Delete(directory, true); }
        }
    }

    private static void TestCharacterHomeFollowsLineage()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-home-lineage-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration node = Register(registry, "first", "one", Guid.NewGuid());
            PartitionOwnership one = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
            PartitionOwnership two = registry.ClaimPartition(2, node.Node, node.Epoch, 0);
            ReportCharacterHome(registry, 42, 7, 900, 1, "first");
            Assert(registry.ResolveClientRoute(42) is { Partition: 1 });
            // The boarding merge: the pilot's own partition folds into the ship's and is retired.
            registry.RecordLineage("board", LineageKind.Merge, [new(1, one.Generation), new(2, two.Generation)],
                [new(2, two.Generation)], null, 10);
            Assert(registry.GetCharacterHome(42) is { Partition: 2, Entity: 900 });
            Assert(registry.ResolveClientRoute(42) is { Partition: 2, Node: "first" });
            // A home persisted before homes were re-pointed still names the retired id: the join
            // follows the lineage to the live partition rather than falling back.
            SeedLegacyCharacterHome(registry, 43, new CharacterHome(8, 901, 1, "first", now));
            Assert(registry.ResolveClientRoute(43) is { Partition: 2, Node: "first" });
            Assert(!registry.PendingHomeRecovery(43));
            // A split sends each home to the output that holds its body.
            registry.RecordLineage("split", LineageKind.Split, [new(2, two.Generation)],
                [new(3, 1), new(4, 1)], new Dictionary<ulong, long[]> { [3] = [900], [4] = [901, 902] }, 20);
            Assert(registry.GetCharacterHome(42) is { Partition: 3 });
            // T-0252: a pilot seated in a ship is not a member - the ship is. A split must send the away
            // pilot's placement AND home to the output whose grid seats it, not the most populated one.
            PartitionOwnership three = registry.ClaimPartition(3, node.Node, node.Epoch, 0);
            ReportCharacterHome(registry, 45, 10, 950, 3, "first");
            registry.RouteClient(45, 3, 950);
            registry.RecordLineage("seated-split", LineageKind.Split, [new(3, three.Generation)],
                [new(20, 1), new(21, 1)], new Dictionary<ulong, long[]> { [20] = [960], [21] = [961, 962, 963] }, 25,
                new Dictionary<ulong, long[]> { [20] = [950], [21] = [] });
            Assert(registry.GetCharacterHome(45) is { Partition: 20, Entity: 950 });
            Assert(registry.GetClientPartition(45) is { Partition: 20, Entity: 950 });
            // Without the seated hint the old fallback (most populated output) still applies.
            registry.ClaimPartition(21, node.Node, node.Epoch, null);
            ReportCharacterHome(registry, 46, 11, 970, 21, "first");
            registry.RecordLineage("unseated-split", LineageKind.Split, [new(21, 1)],
                [new(22, 1), new(23, 1)], new Dictionary<ulong, long[]> { [22] = [961], [23] = [962, 963] }, 26);
            Assert(registry.GetCharacterHome(46) is { Partition: 23 });
            // Home 43 still names the retired 1 (a stale home is only followed, never rewritten in
            // place): two hops, 1 -> 2 -> 4, resolved by the body it holds.
            registry.ClaimPartition(4, node.Node, node.Epoch, 0);
            Assert(registry.ResolveClientRoute(43) is { Partition: 4, Node: "first" });
            SeedLegacyCharacterHome(registry, 44, new CharacterHome(9, 902, 1, "first", now));
            Assert(!registry.PendingHomeRecovery(44));
            Assert(registry.ResolveClientRoute(44) is { Partition: 4, Node: "first" });
            // A home three days old sits hundreds of retirements behind the live partition (w29
            // 2026-09-08: 146-351 hops). Chain 300 merges off partition 4, each retiring the last,
            // and the stale home 43 (1 -> 2 -> 4 -> 1000 -> ... -> 1299) must still land on the live end.
            ulong live = 4;
            for (ulong next = 1000; next <= 1299; next++)
            {
                registry.ClaimPartition(next, node.Node, node.Epoch, 0);
                registry.RecordLineage("chain" + next, LineageKind.Merge, [new(live, 1), new(next, 1)], [new(next, 2)], null, 20 + (long)next);
                live = next;
            }
            Assert(registry.ResolveClientRoute(43) is { Partition: 1299, Node: "first" });
            Assert(registry.ResolveClientRoute(44) is { Partition: 1299, Node: "first" });
            Assert(!registry.PendingHomeRecovery(43));
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>SE-843: a catalogue that stopped advancing shows in the status, and clears with the next save.</summary>
    private static void TestGlobalSaveStale()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-stale-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromHours(1));
            bool Stale() => registry.Lifecycle().DegradedReasons.Contains("globalSaveStale");

            // A cluster without a World Authority has nothing to wait for, however old it gets.
            Register(registry, "node-1", "local", Guid.NewGuid());
            now += TimeSpan.FromHours(1);
            Assert(!Stale() && registry.Lifecycle().GlobalSaveAgeSeconds == null);

            // The clock starts at the registration of an authority that has not saved yet.
            NodeRegistration authority = registry.RegisterNode(Join("wa-1", "wa-one", Guid.NewGuid()) with
                { Role = NodeRole.WA, ControlEndpoint = "http://127.0.0.1:29419" });
            now += TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1);
            Assert(!Stale());
            now += TimeSpan.FromSeconds(1);
            Assert(Stale());

            GlobalSaveId id = new(authority.AuthorityGeneration, 1);
            registry.RegisterGlobalSave(id, authority.Node, authority.Epoch, SaveKind.Periodic, 1, "global", 1, "wa/global-1");
            ClusterLifecycle saved = registry.Lifecycle();
            Assert(saved is { Phase: ClusterPhase.Serving, GlobalSaveAgeSeconds: 0 } && !saved.DegradedReasons.Contains("globalSaveStale"));

            // The refusal this is for: the writer retries a sequence the catalogue has passed and the age keeps growing.
            registry.RegisterGlobalSave(id with { Sequence = 9 }, authority.Node, authority.Epoch, SaveKind.Periodic, 1, "global", 1, "wa/global-9");
            AssertConflict(() => registry.RegisterGlobalSave(id with { Sequence = 2 }, authority.Node, authority.Epoch, SaveKind.Periodic, 1, "global", 1, "wa/global-2"));
            now += TimeSpan.FromMinutes(5);
            ClusterLifecycle stalled = registry.Lifecycle();
            Assert(stalled is { Phase: ClusterPhase.Degraded, GlobalSaveAgeSeconds: 300 } && stalled.DegradedReasons.Contains("globalSaveStale"));
            Admin.ClusterStatus status = registry.AdminStatus(0);
            Assert(status.Health == Admin.AdminHealth.Unhealthy && status.ReasonCodes.Contains("globalSaveStale"));

            // The next save clears it.
            registry.RegisterGlobalSave(id with { Sequence = 10 }, authority.Node, authority.Epoch, SaveKind.Periodic, 1, "global", 1, "wa/global-10");
            Assert(!Stale() && registry.Lifecycle().Phase == ClusterPhase.Serving);

            // A stop owns the phase from here; the age stays readable while it waits for the final save.
            registry.BeginShutdown(false, TimeSpan.FromHours(1));
            now += TimeSpan.FromMinutes(10);
            ClusterLifecycle draining = registry.Lifecycle();
            Assert(!draining.DegradedReasons.Contains("globalSaveStale") && draining.GlobalSaveAgeSeconds == 600);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static void TestWorldAuthority()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-wa-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration authority = registry.RegisterNode(Join("wa-1", "wa-one", Guid.NewGuid()) with
			{ Role = NodeRole.WA, ControlEndpoint = "http://127.0.0.1:29419" });
            Assert(authority is { Role: NodeRole.WA, AuthorityGeneration: > 0 });
            Assert(registry.WorldAuthority() is { Node: "wa-1", Generation: var generation, ControlEndpoint: "http://127.0.0.1:29419" }
				&& generation == authority.AuthorityGeneration);
            // The relay's lock-free per-packet read agrees with the locked one.
            Assert(registry.WorldAuthorityGeneration == authority.AuthorityGeneration);
            // A WA registration against a LIVE authority is busy (503, the registrant retries until the
            // lease lapses), never a definitive 409 — a warm-restarting wa-1 beat its own dying lease
            // and fail-fasted on the 409 (P3-SUN-01).
            AssertBusy(() => registry.RegisterNode(Join("wa-2", "wa-two", Guid.NewGuid()) with { Role = NodeRole.WA }));
            AssertConflict(() => registry.ClaimPartition(1, authority.Node, authority.Epoch, 0));
            AssertConflict(() => registry.AllocatePartitionIds(authority.Node, authority.Epoch, 1));
            GlobalSaveRegistration global = registry.RegisterGlobalSave(new(authority.AuthorityGeneration, 1), authority.Node, authority.Epoch,
                SaveKind.Periodic, 42, "global", 123, "wa/global", "http://127.0.0.1:29419");
            // Idempotent by identity, not by reference: KI-15 hands every caller a snapshot.
            GlobalSaveRegistration globalAgain = registry.RegisterGlobalSave(global.SaveId, authority.Node, authority.Epoch,
                SaveKind.Periodic, 42, "global", 123, "wa/global", "http://127.0.0.1:29419");
            Assert(!ReferenceEquals(global, globalAgain) && globalAgain.SaveId == global.SaveId
                && globalAgain.Locations.SetEquals(global.Locations));
            SnapshotRegistration snapshot = registry.CreateOnlineSnapshot("wa-online", 123);
            Assert(snapshot.GlobalRef == global.SaveId && snapshot.Dirty.Length == 0);

            now += TimeSpan.FromSeconds(6);
            registry.Heartbeat(authority.Node, authority.Epoch, []);
            now += TimeSpan.FromSeconds(5);
            Assert(registry.ExpireLeases() == 0 && registry.WorldAuthority().Node == authority.Node);
            now += TimeSpan.FromSeconds(6);
            registry.ExpireLeases();
            Assert(registry.WorldAuthority() is { Node: null, Generation: var fenced } && fenced > authority.AuthorityGeneration);
            AssertConflict(() => registry.RegisterGlobalSave(new(authority.AuthorityGeneration, 2), authority.Node, authority.Epoch,
                SaveKind.Periodic, 42, "stale", 125, "wa/stale"));

            NodeRegistration replacement = registry.RegisterNode(Join("wa-2", "wa-two", Guid.NewGuid()) with { Role = NodeRole.WA });
            Assert(replacement.AuthorityGeneration > authority.AuthorityGeneration);
            // Compare identity, not the whole record: the write path returns a snapshot (KI-15)
            // whose collection members are fresh instances, and record equality compares those by
            // reference, so a snapshot never equals the live row a read path hands back.
            Assert(registry.NewestGlobalSave() == null && registry.NewestGlobalSaveForRecovery()?.SaveId == global.SaveId);
            SnapshotRegistration replacementSnapshot = registry.CreateOnlineSnapshot("wa-replacement-online", 126);
            Assert(replacementSnapshot.GlobalRef == null && replacementSnapshot.Dirty.Contains("global"));
            GlobalSaveRegistration replacementGlobal = registry.RegisterGlobalSave(new(replacement.AuthorityGeneration, 1), replacement.Node,
                replacement.Epoch, SaveKind.Periodic, 43, "replacement-global", 127, "wa/replacement-global");
            Assert(registry.NewestGlobalSave()?.SaveId == replacementGlobal.SaveId
                && registry.NewestGlobalSaveForRecovery()?.SaveId == replacementGlobal.SaveId);
            AssertConflict(() => registry.Heartbeat(authority.Node, authority.Epoch, []));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestSimulationStall()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-stall-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromMinutes(2));
            NodeRegistration node = Register(registry, "stalled", "local", Guid.NewGuid());
            PartitionOwnership owner = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
            registry.Heartbeat(node.Node, node.Epoch, [new(1, owner.Generation)], simulationFrame: 1);
            now += TimeSpan.FromSeconds(15);
            Assert(registry.Heartbeat(node.Node, node.Epoch, [new(1, owner.Generation)], simulationFrame: 1).State == NodeState.Closed);
            now += TimeSpan.FromSeconds(45);
            Assert(registry.ExpireLeases() == 1 && registry.GetPartitionOwner(1).Node == null);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestVoxelCatalog()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-voxel-self-test-" + Guid.NewGuid());
        try
        {
            VoxelBaseRegistration basis;
            VoxelJournalRegistration journal;
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using (var registry = new GatewayRegistry(directory, Admission, () => now))
            {
                NodeRegistration authority = registry.RegisterNode(Join("wa", "wa", Guid.NewGuid()) with { Role = NodeRole.WA });
                NodeRegistration regular = Register(registry, "node", "node", Guid.NewGuid());
                AssertConflict(() => registry.RegisterVoxelBase("EarthLike", 1, 4, regular.Node, regular.Epoch,
                    authority.AuthorityGeneration, 10, "bad", "node/base"));
                basis = registry.RegisterVoxelBase("EarthLike", 1, 4, authority.Node, authority.Epoch,
                    authority.AuthorityGeneration, 10, "SUM", "wa/base");
                Assert(ReferenceEquals(basis, registry.RegisterVoxelBase("EarthLike", 1, 4, authority.Node, authority.Epoch,
                    authority.AuthorityGeneration, 10, "sum", "wa/base")));
                AssertConflict(() => registry.RegisterVoxelBase("EarthLike", 1, 4, authority.Node, authority.Epoch,
                    authority.AuthorityGeneration, 11, "other", "wa/base"));
                AssertArgument(() => registry.ReportVoxelJournal(new("EarthLike", 2, 5), authority.Node, authority.Epoch, authority.AuthorityGeneration));
                journal = registry.ReportVoxelJournal(new("EarthLike", 1, 5), authority.Node, authority.Epoch, authority.AuthorityGeneration);
                Assert(journal.TailSeq == 5);
                SnapshotRegistration snapshot = registry.CreateOnlineSnapshot("voxel", 1);
                Assert(snapshot.VoxelRefs?.Single() == new VoxelRef("EarthLike", 1, 5) && !snapshot.Dirty.Contains("voxel:EarthLike"));
                AssertArgument(() => registry.CreateSnapshot("voxel-omitted", SnapshotKind.Online, 1, [], []));
                AssertArgument(() => registry.CreateSnapshot("voxel-future", SnapshotKind.Conversion, 1, [], [], null,
                    [new("EarthLike", 1, 6)]));
                AssertConflict(() => registry.ReportVoxelJournal(new("EarthLike", 1, 4), authority.Node, authority.Epoch, authority.AuthorityGeneration));
                AssertConflict(() => registry.RegisterVoxelBase("EarthLike", 2, 4, authority.Node, authority.Epoch,
                    authority.AuthorityGeneration, 10, "next", "wa/base2"));
                registry.RegisterVoxelBase("EarthLike", 2, 5, authority.Node, authority.Epoch,
                    authority.AuthorityGeneration, 10, "next", "wa/base2");
                Assert(registry.CreateOnlineSnapshot("voxel-dirty", 2).Dirty.Contains("voxel:EarthLike"));
                journal = registry.ReportVoxelJournal(new("EarthLike", 2, 6), authority.Node, authority.Epoch, authority.AuthorityGeneration);
                Assert(registry.CreateOnlineSnapshot("voxel-current", 3).VoxelRefs?.Single() == new VoxelRef("EarthLike", 2, 6));
                AssertConflict(() => registry.ReportVoxelReplica("EarthLike", authority.Node, authority.Epoch, 3));
                AssertArgument(() => registry.ReportVoxelReplica(" ", regular.Node, regular.Epoch, 3));
                registry.ReportVoxelReplica("EarthLike", regular.Node, regular.Epoch, 3);
                AssertConflict(() => registry.ReportVoxelReplica("EarthLike", regular.Node, regular.Epoch, 2));
                registry.ReportVoxelReplica("EarthLike", regular.Node, regular.Epoch, 6);
                Assert(registry.VoxelReplicas().Single() is { Node: "node", Applied: 6 });
            }
            now += TimeSpan.FromHours(1);
            using var recovered = new GatewayRegistry(directory, Admission, () => now);
            Assert(recovered.VoxelBases().Length == 2 && recovered.VoxelBases()[0] == basis && recovered.VoxelJournals().Single() == journal);
            // The persisted watermark row survives, but the node's lease does not span the restart, so the
            // safety-filtered view must hide it rather than let a dead node's progress justify a truncation.
            Assert(recovered.VoxelReplicas().Length == 0);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// T-0397: an under-held global save is handed to ONE node at a time (a reservation) and global copies spend their own
    /// byte budget. Before, every live node took the same newest under-held global at once (r91 2026-10-04 10:47: ~10,000
    /// copies of ~1,000 globals in 6 min, every serving node dropping sim seconds every ~8 s). Earned reds: without the
    /// reservation node-2 is handed the global node-1 is copying; without the budget node-2 gets the second one at once.
    /// </summary>
    private static void TestGlobalReplicaReservation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-global-reservation-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        int rate = GatewayRegistry.GlobalBytesPerSecond;
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("global-replica-reservation: " + what); }
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration authority = registry.RegisterNode(Join("wa-1", "wa-one", Guid.NewGuid()) with
                { Role = NodeRole.WA, ControlEndpoint = "http://127.0.0.1:29419" });
            NodeRegistration[] nodes = [.. Enumerable.Range(1, 3).Select(i => registry.RegisterNode(Join("node-" + i, "node-" + i, Guid.NewGuid())
                with { ControlEndpoint = "http://127.0.0.1:2942" + i }))];
            void Wait(int seconds)
            {
                for (int i = 0; i < seconds; i += 5)
                {
                    now += TimeSpan.FromSeconds(5);
                    registry.Heartbeat(authority.Node, authority.Epoch, []);
                    foreach (NodeRegistration node in nodes) registry.Heartbeat(node.Node, node.Epoch, []);
                    registry.ExpireLeases();
                }
            }
            ReplicaDirective? Global(NodeRegistration node) => registry.ReplicaWork(node.Node, node.Epoch).SingleOrDefault(x => x.Global);
            long generation = authority.AuthorityGeneration;
            GlobalSaveRegistration first = registry.RegisterGlobalSave(new(generation, 1), authority.Node, authority.Epoch,
                SaveKind.Periodic, 42, "one", 100, "wa/g1", "http://127.0.0.1:29419");

            ReplicaDirective? taken = Global(nodes[0]);
            Check(taken is { } work && work.SaveId.Generation == generation && work.SaveId.Sequence == 1, "node-1 is handed the under-held global");
            Check(Global(nodes[1]) == null && Global(nodes[2]) == null, "T-0397: nobody else copies the global node-1 is copying");
            Check(Global(nodes[0]) is { SaveId.Sequence: 1 }, "the reserving node may ask again (a retry after a failed copy)");
            registry.RegisterGlobalHolder(first.SaveId, nodes[0].Node, nodes[0].Epoch, 42, "one", "node-1/g1", nodes[0].ControlEndpoint!);
            Check(Global(nodes[1]) == null && Global(nodes[2]) == null, "two holders: nothing more to copy");

            // A copy that never reports back frees the global after the reservation (60 s).
            GlobalSaveRegistration second = registry.RegisterGlobalSave(new(generation, 2), authority.Node, authority.Epoch,
                SaveKind.Periodic, 43, "two", 101, "wa/g2", "http://127.0.0.1:29419");
            Check(Global(nodes[1]) is { SaveId.Sequence: 2 } && Global(nodes[2]) == null, "node-2 reserves the second global");
            Wait(65);
            Check(Global(nodes[2]) is { SaveId.Sequence: 2 }, "an expired reservation frees the global for another node");
            registry.RegisterGlobalHolder(second.SaveId, nodes[2].Node, nodes[2].Epoch, 43, "two", "node-3/g2", nodes[2].ControlEndpoint!);

            // The budget: at 1 byte/s (burst 10 bytes) one 44-byte copy overdraws it; the next waits for the refill.
            GatewayRegistry.GlobalBytesPerSecond = 1;
            Wait(15);
            registry.RegisterGlobalSave(new(generation, 3), authority.Node, authority.Epoch, SaveKind.Periodic, 44, "three", 102, "wa/g3", "http://127.0.0.1:29419");
            registry.RegisterGlobalSave(new(generation, 4), authority.Node, authority.Epoch, SaveKind.Periodic, 44, "four", 103, "wa/g4", "http://127.0.0.1:29419");
            Check(Global(nodes[0]) is { SaveId.Sequence: 4 }, "the newest under-held global goes first");
            Check(Global(nodes[1]) == null, "T-0397: the global budget is spent - the next copy waits");
            Wait(40);
            Check(Global(nodes[1]) is { SaveId.Sequence: 3 }, "the refilled budget hands out the next global");

            // T-0397 (c): with CLUSTER_REPLICA_GLOBAL_NEWEST=1 only the newest global must keep two holders; the older
            // under-held ones (3 and 4, never reported back) are not handed out, and 0 restores "all of them".
            GatewayRegistry.GlobalBytesPerSecond = rate;
            Wait(65);
            GatewayRegistry.GlobalReplicaNewest = 1;
            GlobalSaveRegistration fifth = registry.RegisterGlobalSave(new(generation, 5), authority.Node, authority.Epoch,
                SaveKind.Periodic, 45, "five", 104, "wa/g5", "http://127.0.0.1:29419");
            Check(Global(nodes[0]) is { SaveId.Sequence: 5 }, "newest=1: the newest global is still replicated");
            registry.RegisterGlobalHolder(fifth.SaveId, nodes[0].Node, nodes[0].Epoch, 45, "five", "node-1/g5", nodes[0].ControlEndpoint!);
            Check(Global(nodes[1]) == null && Global(nodes[2]) == null, "newest=1: older under-held globals are not copied");
            GatewayRegistry.GlobalReplicaNewest = 0;
            Check(Global(nodes[1]) is { SaveId.Sequence: 4 }, "newest=0 (today): every under-held global is copied again");
        }
        finally
        {
            GatewayRegistry.GlobalBytesPerSecond = rate;
            GatewayRegistry.GlobalReplicaNewest = 0;
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// T-0399: a node whose scrub finds its copy of a global save unreadable withdraws it (POST
    /// /registry/global-saves/holders?withdraw=1). Two holders, one withdraws: the global is under-held again and a node
    /// is handed a copy from the remaining holder only. A withdraw of a location nobody holds, of another node's
    /// location, of an unknown global, or a second time, changes nothing.
    /// </summary>
    private static void TestGlobalHolderWithdraw()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-global-withdraw-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("global-holder-withdraw: " + what); }
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration authority = registry.RegisterNode(Join("wa-1", "wa-one", Guid.NewGuid()) with
                { Role = NodeRole.WA, ControlEndpoint = "http://127.0.0.1:29419" });
            NodeRegistration[] nodes = [.. Enumerable.Range(1, 2).Select(i => registry.RegisterNode(Join("node-" + i, "node-" + i, Guid.NewGuid())
                with { ControlEndpoint = "http://127.0.0.1:2942" + i }))];
            ReplicaDirective? Global(NodeRegistration node) => registry.ReplicaWork(node.Node, node.Epoch).SingleOrDefault(x => x.Global);
            GlobalSaveRegistration Row(GlobalSaveId id) => registry.GlobalSaves().Single(x => x.SaveId == id);
            GlobalSaveId id = registry.RegisterGlobalSave(new(authority.AuthorityGeneration, 1), authority.Node, authority.Epoch,
                SaveKind.Periodic, 42, "one", 100, "wa/g1", "http://127.0.0.1:29419").SaveId;
            registry.RegisterGlobalHolder(id, nodes[0].Node, nodes[0].Epoch, 42, "one", "node-1/g1", nodes[0].ControlEndpoint!);
            Check(Global(nodes[0]) == null && Global(nodes[1]) == null, "two holders: nothing to copy");

            Check(!registry.WithdrawGlobalHolder(id, nodes[1].Node, nodes[1].Epoch, "node-1/g1"), "another node's location is not withdrawn");
            Check(!registry.WithdrawGlobalHolder(id, nodes[1].Node, nodes[1].Epoch, "node-2/g1"), "a location nobody holds: no-op");
            Check(!registry.WithdrawGlobalHolder(new(authority.AuthorityGeneration, 9), nodes[0].Node, nodes[0].Epoch, "node-1/g1"), "an unknown global: no-op");
            Check(Row(id).LocationHolders.Count == 2 && Global(nodes[1]) == null, "the no-ops left both holders");

            Check(registry.WithdrawGlobalHolder(id, nodes[0].Node, nodes[0].Epoch, "node-1/g1"), "the holder withdraws its copy");
            Check(Row(id) is { Locations: var locations, LocationHolders: var holders, HolderEndpoints: var endpoints }
                && locations.SequenceEqual(["wa/g1"]) && holders.Single().Value == "wa-1" && !endpoints.ContainsKey("node-1"),
                "exactly that holder is dropped, its endpoint with it");
            Check(!registry.WithdrawGlobalHolder(id, nodes[0].Node, nodes[0].Epoch, "node-1/g1"), "a second withdraw is a no-op");
            ReplicaDirective? again = Global(nodes[0]);
            Check(again is { SaveId.Sequence: 1 } work && work.Sources.Single().Holder == "wa-1",
                "one holder left: the global is under-held and handed out again, from the remaining holder only");
            AssertConflict(() => registry.WithdrawGlobalHolder(id, nodes[0].Node, nodes[0].Epoch + 1, "node-1/g1"));
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// T-0230: a WA killed within a second of a periodic save leaves a save only it holds. The
    /// replacement must restore the newest save a live node still holds, not retry the dead WA
    /// (bench 2026-09-24 05:02: seq 4 held by wa-1 only, seq 3 also on node-2).
    /// </summary>
    private static void TestWorldAuthorityRecoveryHolder()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-wa-recovery-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration authority = registry.RegisterNode(Join("wa-1", "wa-one", Guid.NewGuid()) with
                { Role = NodeRole.WA, ControlEndpoint = "http://127.0.0.1:29419" });
            NodeRegistration regular = registry.RegisterNode(Join("node-1", "node-one", Guid.NewGuid()));
            long generation = authority.AuthorityGeneration;
            GlobalSaveRegistration replicated = registry.RegisterGlobalSave(new(generation, 1), authority.Node, authority.Epoch,
                SaveKind.Periodic, 42, "one", 100, "wa/g1", "http://127.0.0.1:29419");
            registry.RegisterGlobalHolder(replicated.SaveId, regular.Node, regular.Epoch, 42, "one", "node/g1", "http://127.0.0.1:29417");
            GlobalSaveRegistration unreplicated = registry.RegisterGlobalSave(new(generation, 2), authority.Node, authority.Epoch,
                SaveKind.Periodic, 43, "two", 101, "wa/g2", "http://127.0.0.1:29419");
            // Its holder is alive: the newest save, replicated or not.
            Assert(registry.NewestGlobalSaveForRecovery()?.SaveId == unreplicated.SaveId);
            // #1270: the World Authority asks what would be restored without it - the replicated save, until node-1 holds g2.
            Assert(registry.NewestGlobalSaveForRecovery(authority.Node)?.SaveId == replicated.SaveId);
            registry.RegisterGlobalHolder(unreplicated.SaveId, regular.Node, regular.Epoch, 43, "two", "node/g2", "http://127.0.0.1:29417");
            Assert(registry.NewestGlobalSaveForRecovery(authority.Node)?.SaveId == unreplicated.SaveId);
            registry.WithdrawGlobalHolder(unreplicated.SaveId, regular.Node, regular.Epoch, "node/g2");

            // wa-1 dies (no more heartbeats); node-1 keeps its lease.
            for (int i = 0; i < 4; i++)
            {
                now += TimeSpan.FromSeconds(5);
                registry.Heartbeat(regular.Node, regular.Epoch, []);
                registry.ExpireLeases();
            }
            Assert(registry.WorldAuthority() is { Node: null, Generation: var fenced } && fenced > generation);
            GlobalSaveRegistration? recovery = registry.NewestGlobalSaveForRecovery();
            Assert(recovery?.SaveId == replicated.SaveId);

            // node-1 dies too: nothing reachable, so the newest catalogued save (a foreign path may serve it).
            for (int i = 0; i < 4; i++)
            {
                now += TimeSpan.FromSeconds(5);
                registry.ExpireLeases();
            }
            Assert(registry.NewestGlobalSaveForRecovery()?.SaveId == unreplicated.SaveId);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// #1318: the World Authority journals its commands between global saves. Only the current WA appends (a stale
    /// epoch or a fenced generation is a conflict); a replacement reads what follows its restored ledger order; a
    /// replacement that numbers its own commands from the restored save drops the older generation's entries from its
    /// first order on; the capacity drops the oldest and says how far.
    /// </summary>
    private static void TestGlobalJournal()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-global-journal-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        static GlobalJournalEntry Entry(long order, string payload = "{}") => new(order, "banking", Guid.NewGuid(), "node-1", payload, "ok");
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration authority = registry.RegisterNode(Join("wa-1", "wa-one", Guid.NewGuid()) with { Role = NodeRole.WA });
            NodeRegistration regular = registry.RegisterNode(Join("node-1", "node-one", Guid.NewGuid()));
            long generation = authority.AuthorityGeneration;
            Assert(registry.AppendGlobalJournal(authority.Node, authority.Epoch, generation, [Entry(3), Entry(1), Entry(2)]) == 3);
            AssertConflict(() => registry.AppendGlobalJournal(regular.Node, regular.Epoch, generation, [Entry(4)]));
            AssertConflict(() => registry.AppendGlobalJournal(authority.Node, authority.Epoch, generation + 1, [Entry(4)]));
            AssertArgument(() => registry.AppendGlobalJournal(authority.Node, authority.Epoch, generation, [Entry(4), Entry(4)]));
            // A retried append (its answer was lost) replaces the entries it already wrote.
            registry.AppendGlobalJournal(authority.Node, authority.Epoch, generation, [Entry(3, "again")]);
            GlobalJournalPage page = registry.GlobalJournal(1);
            Assert(page.Entries.Select(entry => entry.Order).SequenceEqual([2L, 3L]) && page.Entries[1].Payload == "again"
                && page.Entries.All(entry => entry.Generation == generation) && page.DroppedThrough == 0);

            // wa-1 dies: its lease lapses, it is fenced, and an append from it is refused.
            for (int i = 0; i < 4; i++)
            {
                now += TimeSpan.FromSeconds(5);
                registry.Heartbeat(regular.Node, regular.Epoch, []);
                registry.ExpireLeases();
            }
            AssertConflict(() => registry.AppendGlobalJournal(authority.Node, authority.Epoch, generation, [Entry(4)]));
            NodeRegistration replacement = registry.RegisterNode(Join("wa-2", "wa-two", Guid.NewGuid()) with { Role = NodeRole.WA });
            Assert(replacement.AuthorityGeneration > generation);
            // wa-2 restored a save at order 1 and could not read the journal: its order 2 supersedes wa-1's 2 and 3.
            registry.AppendGlobalJournal(replacement.Node, replacement.Epoch, replacement.AuthorityGeneration, [Entry(2, "wa-2")]);
            page = registry.GlobalJournal(0);
            Assert(page.Entries.Select(entry => (entry.Order, entry.Generation)).SequenceEqual(
                [(1L, generation), (2L, replacement.AuthorityGeneration)]) && page.Entries[1].Payload == "wa-2");

            registry.AppendGlobalJournal(replacement.Node, replacement.Epoch, replacement.AuthorityGeneration,
                Enumerable.Range(3, GatewayRegistry.GlobalJournalCapacity).Select(order => Entry(order)).ToArray());
            page = registry.GlobalJournal(0);
            Assert(page.Entries.Length == GatewayRegistry.GlobalJournalCapacity && page.DroppedThrough == 2 && page.Entries[0].Order == 3);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestWorldAuthorityShutdown()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-wa-shutdown-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using (var registry = new GatewayRegistry(directory, Admission, () => now))
            {
                NodeRegistration authority = registry.RegisterNode(Join("wa", "wa", Guid.NewGuid()) with { Role = NodeRole.WA });
                registry.RegisterGlobalSave(new(authority.AuthorityGeneration, 1), authority.Node, authority.Epoch,
                    SaveKind.Periodic, 1, "periodic", 1, "wa/periodic");
                registry.RegisterVoxelBase("EarthLike", 1, 4, authority.Node, authority.Epoch,
                    authority.AuthorityGeneration, 10, "sum", "wa/base");
                registry.ReportVoxelJournal(new("EarthLike", 1, 5), authority.Node, authority.Epoch, authority.AuthorityGeneration);
                registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                Assert(!registry.ShutdownReady());
                AssertConflict(() => registry.CompleteShutdown(false));
                registry.RegisterGlobalSave(new(authority.AuthorityGeneration, 2), authority.Node, authority.Epoch,
                    SaveKind.Final, 1, "final", 2, "wa/final");
                registry.MarkEmpty(authority.Node, authority.Epoch);
                Assert(registry.ShutdownReady());
                ClusterLifecycle down = registry.CompleteShutdown(false);
                Assert(down.LastShutdown is { Dirty.Length: 0, GlobalRef: var global }
                    && global == new GlobalSaveId(authority.AuthorityGeneration, 2)
                    && down.LastShutdown.VoxelRefs?.Single() == new VoxelRef("EarthLike", 1, 5));
            }
            using var warm = new GatewayRegistry(directory, Admission, () => now);
            Assert(warm.Lifecycle() is { Startup: StartupKind.Warm, LastShutdown.GlobalRef.Sequence: 2 });
        }
        finally { Directory.Delete(directory, true); }
    }

    // SE-892: a Gateway whose World Authority never ran has nothing global to lose, so the Final save of the clean stop
    // it started from still stands at its own stop (an update whose nodes all died at startup, then abandoned). Any
    // doubt about that stays dirty: an authority that ran, a Recovery start, a respawned Gateway.
    private static void TestGlobalSaveStandsWithoutAuthority()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var first = new ManagedDeployment("first", Guid.NewGuid(),
            [new("regular", "host", "regular", "127.0.0.1:28417", NodeRole.Regular),
             new("wa", "host", "wa", "127.0.0.1:28418", NodeRole.WA)]);
        ManagedDeployment Next(bool recover = false) => first with { Revision = "next-" + Guid.NewGuid(), StartGeneration = Guid.NewGuid(), Recover = recover, RecoveryUpdate = recover };
        GatewayRegistry Open(string root, ManagedDeployment deployment) => new(root, Admission, () => now, managedDeployment: deployment);
        ShutdownMarker Stop(GatewayRegistry registry)
        {
            registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
            now += TimeSpan.FromSeconds(1);
            return registry.CompleteShutdown(false).LastShutdown!;
        }
        NodeRegistration Authority(GatewayRegistry registry) => registry.RegisterNode(Join("wa", "127.0.0.1:28418", Guid.NewGuid()) with { Role = NodeRole.WA });
        void Quiesce(GatewayRegistry registry, NodeRegistration authority)
        {
            registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
            registry.MarkEmpty(authority.Node, authority.Epoch);
        }
        void Case(Action<string, GlobalSaveId> body)
        {
            string root = Path.Combine(Path.GetTempPath(), "cluster-global-stands-" + Guid.NewGuid());
            try
            {
                GlobalSaveId final;
                using (GatewayRegistry registry = Open(root, first))
                {
                    NodeRegistration regular = Register(registry, "regular", "127.0.0.1:28417", Guid.NewGuid());
                    NodeRegistration authority = Authority(registry);
                    PartitionOwnership owner = registry.ClaimPartition(1, regular.Node, regular.Epoch, 0);
                    registry.RegisterVoxelBase("EarthLike", 1, 4, authority.Node, authority.Epoch, authority.AuthorityGeneration, 10, "sum", "wa/base");
                    registry.ReportVoxelJournal(new("EarthLike", 1, 5), authority.Node, authority.Epoch, authority.AuthorityGeneration);
                    registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                    now += TimeSpan.FromSeconds(1);
                    registry.RegisterSave(new SaveId(1, owner.Generation, 1), regular.Node, regular.Epoch, SaveKind.Final, 1, "final", 1, "node/final");
                    registry.ReleasePartition(1, regular.Node, regular.Epoch, owner.Generation);
                    registry.MarkEmpty(regular.Node, regular.Epoch);
                    final = new GlobalSaveId(authority.AuthorityGeneration, 1);
                    registry.RegisterGlobalSave(final, authority.Node, authority.Epoch, SaveKind.Final, 1, "final", 1, "wa/final");
                    registry.MarkEmpty(authority.Node, authority.Epoch);
                    Assert(registry.CompleteShutdown(false).LastShutdown is { Dirty.Length: 0 });
                }
                now += TimeSpan.FromMinutes(1);
                body(root, final);
            }
            finally { Directory.Delete(root, true); }
        }

        // The failed start, stopped twice over: each stop is clean and the next generation starts warm from it.
        Case((root, final) =>
        {
            foreach (int attempt in new[] { 1, 2 })
                using (GatewayRegistry registry = Open(root, Next()))
                {
                    Assert(registry.Lifecycle().Startup == StartupKind.Warm);
                    now += TimeSpan.FromMinutes(1);
                    registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                    Assert(registry.ShutdownReady());
                    ShutdownMarker marker = registry.CompleteShutdown(false).LastShutdown!;
                    Assert(marker.Dirty.Length == 0 && marker.GlobalRef == final);
                    Assert(registry.AdminSnapshots().Single(x => x.ClusterTime == marker.Timestamp.UtcTicks).HasGlobal);
                }
            using GatewayRegistry warm = Open(root, Next());
            Assert(warm.Lifecycle() is { Startup: StartupKind.Warm, LastShutdown.Dirty.Length: 0 });
        });

        // A World Authority that ran and did not save: its state is gone, whatever the catalogue still holds.
        Case((root, final) =>
        {
            using (GatewayRegistry registry = Open(root, Next()))
            {
                Quiesce(registry, Authority(registry));
                now += TimeSpan.FromSeconds(1);
                Assert(registry.CompleteShutdown(false).LastShutdown is { GlobalRef: null } marker && marker.Dirty.Contains("global"));
            }
            AssertConflict(() => { using GatewayRegistry denied = Open(root, Next()); });
        });

        // One that saved only before the stop began, Final or not, ran on after that save.
        Case((root, final) =>
        {
            using GatewayRegistry registry = Open(root, Next());
            NodeRegistration authority = Authority(registry);
            registry.RegisterGlobalSave(new(authority.AuthorityGeneration, 1), authority.Node, authority.Epoch, SaveKind.Final, 1, "final", 1, "wa/early");
            now += TimeSpan.FromSeconds(1);
            Quiesce(registry, authority);
            Assert(registry.CompleteShutdown(false).LastShutdown!.Dirty.Contains("global"));
        });

        // One that died: its lease lapsing bumps the generation again, with the old Final still the newest row.
        Case((root, final) =>
        {
            using GatewayRegistry registry = Open(root, Next());
            Authority(registry);
            now += TimeSpan.FromMinutes(5);
            registry.ExpireLeases();
            Assert(Stop(registry).Dirty.Contains("global"));
        });

        // Regular nodes without an authority: global state only lives on the authority, so their partitions decide alone.
        Case((root, final) =>
        {
            using (GatewayRegistry registry = Open(root, Next()))
            {
                NodeRegistration regular = Register(registry, "regular", "127.0.0.1:28417", Guid.NewGuid());
                PartitionOwnership owner = registry.ClaimRecoveries(regular.Node, regular.Epoch, [1]).Single().Ownership;
                registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                now += TimeSpan.FromSeconds(1);
                registry.RegisterSave(new SaveId(1, owner.Generation, 1), regular.Node, regular.Epoch, SaveKind.Final, 1, "final", 2, "node/final2");
                registry.ReleasePartition(1, regular.Node, regular.Epoch, owner.Generation);
                registry.MarkEmpty(regular.Node, regular.Epoch);
                Assert(registry.CompleteShutdown(false).LastShutdown is { Dirty.Length: 0 } marker && marker.GlobalRef == final);
            }
            using GatewayRegistry next = Open(root, Next());
            Register(next, "regular", "127.0.0.1:28417", Guid.NewGuid());
            next.BeginShutdown(false, TimeSpan.FromMinutes(1));
            now += TimeSpan.FromMinutes(5);
            next.ExpireLeases();
            Assert(next.CompleteShutdown(false).LastShutdown is { Dirty: ["node:regular"] });
        });

        // A respawned Gateway cannot vouch for what ran before it: an authority's registration can be a row it lost.
        Case((root, final) =>
        {
            ManagedDeployment next = Next();
            using (GatewayRegistry before = Open(root, next)) before.Flush();
            using GatewayRegistry respawned = Open(root, next);
            Assert(respawned.Lifecycle().Startup == StartupKind.Warm);
            Assert(Stop(respawned).Dirty.Contains("global"));
        });

        // A Recovery start stays one until an authority has run and saved, also when the stop before it was clean.
        Case((root, final) =>
        {
            using GatewayRegistry registry = Open(root, Next(recover: true));
            Assert(registry.Lifecycle().Startup == StartupKind.Recovery);
            Assert(Stop(registry).Dirty.Contains("global"));
        });

        // Without a deployment: the same stop is clean when it follows a clean one, dirty after a crash.
        string plain = Path.Combine(Path.GetTempPath(), "cluster-global-stands-" + Guid.NewGuid());
        try
        {
            using (var registry = new GatewayRegistry(plain, Admission, () => now))
            {
                NodeRegistration authority = registry.RegisterNode(Join("wa", "wa", Guid.NewGuid()) with { Role = NodeRole.WA });
                registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                registry.RegisterGlobalSave(new(authority.AuthorityGeneration, 1), authority.Node, authority.Epoch, SaveKind.Final, 1, "final", 1, "wa/final");
                registry.MarkEmpty(authority.Node, authority.Epoch);
                Assert(registry.CompleteShutdown(false).LastShutdown is { Dirty.Length: 0 });
            }
            now += TimeSpan.FromMinutes(1);
            using (var registry = new GatewayRegistry(plain, Admission, () => now))
                Assert(Stop(registry) is { Dirty.Length: 0, GlobalRef.Sequence: 1 });
            using (var registry = new GatewayRegistry(plain, Admission, () => now))
                Assert(registry.Lifecycle().Startup == StartupKind.Warm);   // consumed, then "crashed": no stop
            using (var registry = new GatewayRegistry(plain, Admission, () => now))
            {
                Assert(registry.Lifecycle().Startup == StartupKind.Recovery);
                Assert(Stop(registry).Dirty.Contains("global"));
            }
        }
        finally { Directory.Delete(plain, true); }
    }

    private static void TestNodePlan()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-plan-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now);
            NodeSlotPlan first = registry.PutNodeSlot("slot-1", "host", NodeRole.Regular, NodeDesiredState.Wanted);
            Assert(ReferenceEquals(first, registry.PutNodeSlot("slot-1", "host", NodeRole.Regular, NodeDesiredState.Wanted)));
            registry.PutNodeSlot("slot-2", "host", NodeRole.Regular, NodeDesiredState.Wanted);
            Assert(registry.NodePlan().Select(x => x.SlotKey).SequenceEqual(["slot-1", "slot-2"]));

            registry.ExecutorHeartbeat("executor", [new("slot-1", "a1", SpawnState.Failed, null, "bad-config")]);
            Assert(first.RetryAt == now + TimeSpan.FromSeconds(5) && first.FailureCount == 1);
            registry.ExecutorHeartbeat("executor", [new("slot-1", "a1", SpawnState.Failed, null, "bad-config")]);
            Assert(first.FailureCount == 1);
            now += TimeSpan.FromSeconds(5);
            registry.ExecutorHeartbeat("executor", [new("slot-1", "a2", SpawnState.Failed, null, "bad-config")]);
            Assert(first.RetryAt == now + TimeSpan.FromSeconds(10));
            now += TimeSpan.FromSeconds(10);
            registry.ExecutorHeartbeat("executor", [new("slot-1", "a3", SpawnState.Failed, null, "bad-config"),
                new("slot-2", "b1", SpawnState.Failed, null, "bad-config")]);
            Assert(first.FailureAlert && registry.Lifecycle().GlobalSpawnHalted && !registry.NodePlan().Any(x => x.SpawnAllowed));
            Assert(GatewayRegistry.SpawnBackoff(20) == TimeSpan.FromMinutes(10));

            registry.ExecutorHeartbeat("executor", [new("slot-1", "a4", SpawnState.Ready, "node-1", null)]);
            Assert(!registry.Lifecycle().GlobalSpawnHalted);
            now += TimeSpan.FromSeconds(60);
            Assert(registry.Lifecycle().ExecutorSilent);
            registry.ExecutorHeartbeat("executor", []);
            Assert(!registry.Lifecycle().ExecutorSilent);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// KI-15: no registry API may hand out a live SaveRegistration. The response is serialised by
    /// WriteJson OUTSIDE the lock, and SaveRegistration carries four mutable collections, so a
    /// concurrent replica registration threw "Collection was modified" out of
    /// HashSet&lt;string&gt;.Enumerator.MoveNext inside System.Text.Json and failed a node's
    /// /registry/recoveries/claim with a 500 (node-1, 2026-08-22 06:19:12). ToArray() on the
    /// outside is not a snapshot of the inside. This asserts isolation on the three paths that
    /// were still returning live references: ClaimRecoveries, Cover and the public ResolveCover.
    /// </summary>
    /// <summary>
    /// The cover walk through splits and merges, now served by the per-partition producer index
    /// (99fa47e). A partition with no save of its own resolves to its split parent's save filtered
    /// to the members it took; a merge output resolves to every input's cover. A 400-record chain
    /// of retirements behind the live end - the shape that spun the 2026-09-08 cold start for
    /// minutes per partition - resolves in well under a second.
    /// </summary>
    private static void TestLineageCoverWalk()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-cover-walk-self-test-" + Guid.NewGuid());
        try
        {
            using var registry = new GatewayRegistry(directory, Admission);
            NodeRegistration node = Register(registry, "node-1", "127.0.0.1:28417", Guid.NewGuid());
            PartitionOwnership one = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
            registry.RegisterSave(new SaveId(1, one.Generation, 1), node.Node, node.Epoch,
                SaveKind.Periodic, 1, "checksum", 1, "node-1/save-1");
            // Split 1 -> 10 (members 900) + 11 (members 901, 902); neither output has a save.
            registry.RecordLineage("split", LineageKind.Split, [new(1, one.Generation)],
                [new(10, 1), new(11, 1)], new Dictionary<ulong, long[]> { [10] = [900], [11] = [901, 902] }, 20);
            SaveCover[] ten = registry.ResolveCover(10);
            Assert(ten.Length == 1 && ten[0].Save.SaveId.Partition == 1
                && ten[0].EntityIds is [900]);                      // the parent's save, filtered to 10's members
            SaveCover[] eleven = registry.ResolveCover(11);
            Assert(eleven.Length == 1 && eleven[0].EntityIds is [901, 902]);
            // Merge 10 + 11 -> 12: the cover is both inputs' covers.
            registry.RecordLineage("merge", LineageKind.Merge, [new(10, 1), new(11, 1)],
                [new(12, 1)], null, 30);
            SaveCover[] twelve = registry.ResolveCover(12);
            Assert(twelve.Length == 2 && twelve.All(c => c.Save.SaveId.Partition == 1));
            Assert(twelve.Any(c => c.EntityIds is [900]) && twelve.Any(c => c.EntityIds is [901, 902]));
            // A long chain of merges retiring the last (the live end hundreds of hops from the
            // save) still resolves - and fast.
            ulong live = 12;
            for (ulong next = 13; next <= 412; next++)
            {
                registry.RecordLineage("chain" + next, LineageKind.Merge, [new(live, 1)], [new(next, 1)], null, 40 + (long)next);
                live = next;
            }
            Stopwatch clock = Stopwatch.StartNew();
            SaveCover[] far = registry.ResolveCover(live);
            Assert(far.Length == 2 && far.All(c => c.Save.SaveId.Partition == 1));
            Assert(clock.ElapsedMilliseconds < 1000);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* best effort */ }
        }
    }

    private static void TestSaveResponseIsolation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-save-isolation-self-test-" + Guid.NewGuid());
        try
        {
            using var registry = new GatewayRegistry(directory, Admission);
            NodeRegistration node = Register(registry, "node-1", "127.0.0.1:28417", Guid.NewGuid());
            PartitionOwnership owner = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
            registry.RegisterSave(new SaveId(1, owner.Generation, 1), node.Node, node.Epoch,
                SaveKind.Periodic, 1, "checksum", 1, "node-1/save-1");

            // Every response object, captured before the registry is mutated.
            SaveCover[] resolved = registry.ResolveCover(1);
            SaveRegistration[] covered = registry.Cover([1]);
            Assert(resolved.Length == 1 && covered.Length == 1);   // cover paths must see the save

            int resolvedBefore = resolved[0].Save.Locations.Count;
            int coveredBefore = covered[0].Locations.Count;

            // A second holder registering is the exact concurrent mutation that threw: it adds to
            // the live save's Locations while a response holding that same object is serialised.
            NodeRegistration second = Register(registry, "node-2", "127.0.0.1:28418", Guid.NewGuid());
            registry.RegisterHolder(new SaveId(1, owner.Generation, 1), second.Node, second.Epoch,
                1, "checksum", "node-2/save-1", "127.0.0.1:28418");

            Assert(resolved[0].Save.Locations.Count == resolvedBefore);   // ResolveCover response must not observe a later holder
            Assert(covered[0].Locations.Count == coveredBefore);   // Cover response must not observe a later holder

            // Same for the recovery-claim path, which is what actually 500'd in production.
            registry.ReleasePartition(1, node.Node, node.Epoch, owner.Generation);
            RecoveryClaim[] claims = registry.ClaimRecoveries(second.Node, second.Epoch, [1]);
            Assert(claims.Length == 1 && claims[0].ResolveCover.Length >= 1);   // claim must carry a cover
            SaveRegistration claimed = claims[0].ResolveCover[0].Save;
            int claimedBefore = claimed.Locations.Count;
            NodeRegistration third = Register(registry, "node-3", "127.0.0.1:28419", Guid.NewGuid());
            // The claim bumped the generation, so use the save the claim actually named.
            registry.RegisterHolder(claimed.SaveId, third.Node, third.Epoch,
                claimed.SizeBytes, claimed.Checksum, "node-3/save-1", "127.0.0.1:28419");
            Assert(claimed.Locations.Count == claimedBefore);   // ClaimRecoveries response must not observe a later holder

            // And the copy must be faithful, not merely detached.
            Assert(claimed.Locations.Contains("node-1/save-1"));   // the snapshot must carry the real contents
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// T-0223: a drain releases a member-less split output before its first save. It is unowned, has
    /// no catalogued save and can never get one once every node is Closed, so the stop must not wait
    /// for it (2026-09-23 06:57: 120 s, then the gateway was SIGKILLed). It is still recorded dirty,
    /// so the next start is a Recovery start that recovers it through the split lineage.
    /// </summary>
    private static void TestShutdownUnsavedSplitOutput()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using (var registry = new GatewayRegistry(directory, Admission, () => now))
            {
                NodeRegistration node = Register(registry, "node", "local", Guid.NewGuid());
                PartitionOwnership parent = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
                registry.RegisterSave(new SaveId(1, parent.Generation, 1), node.Node, node.Epoch, SaveKind.Periodic, 1, "p", 1, "node/p");
                PartitionOwnership kept = registry.ClaimPartition(10, node.Node, node.Epoch, 0);
                PartitionOwnership empty = registry.ClaimPartition(11, node.Node, node.Epoch, 0);
                registry.RecordLineage("split", LineageKind.Split, [new(1, parent.Generation)],
                    [new(10, kept.Generation), new(11, empty.Generation)], new Dictionary<ulong, long[]> { [10] = [900], [11] = [901] }, 20);
                registry.RegisterSave(new SaveId(10, kept.Generation, 1), node.Node, node.Epoch, SaveKind.Final, 1, "f", 2, "node/f");
                registry.ReleasePartition(10, node.Node, node.Epoch, kept.Generation);
                registry.ReleasePartition(11, node.Node, node.Epoch, empty.Generation);   // the member-less drain release
                registry.ReleasePartition(1, node.Node, node.Epoch, parent.Generation);   // retired by the split
                Assert(registry.GetPartitionOwner(11) is { Node: null });                   // kept: its lineage protects it
                registry.CloseNode(node.Node, node.Epoch, TimeSpan.FromSeconds(5));
                registry.MarkEmpty(node.Node, node.Epoch);
                registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                Assert(registry.ShutdownReady());
                ClusterLifecycle down = registry.CompleteShutdown(false);
                Assert(down.Phase == ClusterPhase.Down && down.LastShutdown!.Dirty.Contains("partition:11"));
            }
            using var next = new GatewayRegistry(directory, Admission, () => now);
            Assert(next.Lifecycle().Startup == StartupKind.Recovery);
            Assert(next.ResolveCover(11).Length == 1);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// #1292: the same release, flagged member-less by the drain, reaps the row - the stop is clean and the start
    /// after it is not a Recovery start (octahedron-1191 2026-10-21: 836 and 922 dirty at every stop). A piece whose
    /// cover holds a character's home body is kept: that body is content the stop's disconnect removed.
    /// </summary>
    private static void TestShutdownMemberlessUnsaved()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            foreach (bool home in new[] { false, true })
            {
                string run = Path.Combine(directory, home ? "home" : "plain");
                using (var registry = new GatewayRegistry(run, Admission, () => now))
                {
                    NodeRegistration node = Register(registry, "node", "local", Guid.NewGuid());
                    PartitionOwnership parent = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
                    registry.RegisterSave(new SaveId(1, parent.Generation, 1), node.Node, node.Epoch, SaveKind.Periodic, 2, "p", 1, "node/p");
                    if (home) ReportCharacterHome(registry, 42, 7, 901, 1, node.Node);
                    PartitionOwnership kept = registry.ClaimPartition(10, node.Node, node.Epoch, 0);
                    PartitionOwnership piece = registry.ClaimPartition(11, node.Node, node.Epoch, 0);
                    registry.RecordLineage("split", LineageKind.Split, [new(1, parent.Generation)],
                        [new(10, kept.Generation), new(11, piece.Generation)], new Dictionary<ulong, long[]> { [10] = [900], [11] = [901] }, 20);
                    registry.RegisterSave(new SaveId(10, kept.Generation, 1), node.Node, node.Epoch, SaveKind.Final, 1, "f", 2, "node/f");
                    registry.ReleasePartition(10, node.Node, node.Epoch, kept.Generation, memberless: true);   // has its own save: stays
                    registry.ReleasePartition(11, node.Node, node.Epoch, piece.Generation, memberless: true);
                    registry.ReleasePartition(1, node.Node, node.Epoch, parent.Generation);
                    Assert(registry.GetPartitionOwner(10) is { Node: null });
                    Assert(registry.Partitions().Any(x => x.Partition == 11) == home);
                    registry.CloseNode(node.Node, node.Epoch, TimeSpan.FromSeconds(5));
                    registry.MarkEmpty(node.Node, node.Epoch);
                    registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                    Assert(registry.ShutdownReady());
                    ClusterLifecycle down = registry.CompleteShutdown(false);
                    Assert(down.Phase == ClusterPhase.Down);
                    Assert(down.LastShutdown!.Dirty.SequenceEqual(home ? ["partition:11"] : []));
                }
                using var next = new GatewayRegistry(run, Admission, () => now);
                Assert(next.Lifecycle().Startup == (home ? StartupKind.Recovery : StartupKind.Warm));
                Assert(next.Partitions().Any(x => x.Partition == 11) == home);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestClusterShutdown()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-shutdown-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
		long finalGeneration = 0;
        try
        {
            using (var recovery = new GatewayRegistry(directory, Admission, () => now))
                Assert(recovery.Lifecycle().Startup == StartupKind.Recovery);
            using (var registry = new GatewayRegistry(directory, Admission, () => now))
            {
                NodeRegistration node = Register(registry, "node", "local", Guid.NewGuid());
                PartitionOwnership owner = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
				finalGeneration = owner.Generation;
                // A partition released unowned with only a periodic save (the rotator's "released
                // stranded partition for recovery", or a drain's member-less release) that nobody
                // re-claims before the stop closes the nodes: its newest save is its state and must
                // not hold the shutdown (2026-09-07 21:19: 600 s wait with every node Empty).
                PartitionOwnership stranded = registry.ClaimPartition(2, node.Node, node.Epoch, 0);
                registry.RegisterSave(new SaveId(2, stranded.Generation, 1), node.Node, node.Epoch, SaveKind.Periodic, 1, "periodic", 1, "node/periodic");
                registry.ReleasePartition(2, node.Node, node.Epoch, stranded.Generation);
                Assert(registry.GetPartitionOwner(2).Node == null);
                registry.Heartbeat(node.Node, node.Epoch, [new(1, owner.Generation)], saveFailure: new(1, "disk full", 2));
                Assert(node.State == NodeState.Closed && node.LastSaveFailure?.Attempts == 2
                    && registry.Lifecycle().DegradedReasons.Contains("saveFailure"));
                registry.RegisterSave(new SaveId(1, owner.Generation, 1), node.Node, node.Epoch, SaveKind.Final, 1, "final", 1, "node/final");
                registry.ReleasePartition(1, node.Node, node.Epoch, owner.Generation);
                registry.MarkEmpty(node.Node, node.Epoch);
                Assert(registry.AdmissionOpen);
                registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                Assert(!registry.AdmissionOpen && registry.ShutdownReady());
                ClusterLifecycle down = registry.CompleteShutdown(false);
                Assert(down.Phase == ClusterPhase.Down && down.LastShutdown is { Dirty.Length: 0, Manifest.Length: 2 }
                    && down.LastShutdown.Manifest.Any(x => x.Partition == 2 && x.Sequence == 1));
            }
            using (var warm = new GatewayRegistry(directory, Admission, () => now))
            {
                Assert(warm.Lifecycle().Startup == StartupKind.Warm && warm.Lifecycle().LastShutdown?.Dirty.Length == 0);
                NodeRegistration restarted = Register(warm, "warm", "warm", Guid.NewGuid());
                RecoveryClaim claim = warm.ClaimRecoveries(restarted.Node, restarted.Epoch, [1]).Single();
                Assert(claim.Ownership.Generation == finalGeneration + 1 && claim.ResolveCover.Single().Save.SaveId == new SaveId(1, finalGeneration, 1));
                RecoveryClaim strandedClaim = warm.ClaimRecoveries(restarted.Node, restarted.Epoch, [2]).Single();
                Assert(strandedClaim.ResolveCover.Single().Save.SaveId.Partition == 2 && strandedClaim.ResolveCover.Single().Save.Kind == SaveKind.Periodic);

                // 2026-09-24 03:17: a node that dies during the stop (lease expired) can never become
                // Empty. The graceful path must complete - dirty, naming the node - not refuse until
                // the caller SIGKILLs everything. Its partition is not waited for either.
                NodeRegistration doomed = Register(warm, "doomed", "doomed", Guid.NewGuid());
                PartitionOwnership held = warm.ClaimPartition(3, doomed.Node, doomed.Epoch, 0);
                warm.RegisterSave(new SaveId(3, held.Generation, 1), doomed.Node, doomed.Epoch, SaveKind.Periodic, 1, "periodic", 1, "doomed/periodic");
                foreach (ulong partition in new ulong[] { 1, 2 })
                {
                    PartitionOwnership owned = warm.GetPartitionOwner(partition);
                    warm.RegisterSave(new SaveId(partition, owned.Generation, 1), restarted.Node, restarted.Epoch, SaveKind.Final, 1, "final", 1, "warm/final");
                    warm.ReleasePartition(partition, restarted.Node, restarted.Epoch, owned.Generation);
                }
                warm.BeginShutdown(false, TimeSpan.FromMinutes(1));
                warm.MarkEmpty(restarted.Node, restarted.Epoch);
                Assert(!warm.ShutdownReady());
                now += TimeSpan.FromMinutes(5);
                warm.ExpireLeases();
                Assert(doomed.State == NodeState.Dead && warm.ShutdownReady());
                ClusterLifecycle down = warm.CompleteShutdown(false);
                Assert(down.Phase == ClusterPhase.Down && down.LastShutdown!.Dirty.Contains("node:doomed"));
            }
            using var again = new GatewayRegistry(directory, Admission, () => now);
            // Dirty (a node died) means a Recovery start, and the marker says why.
            Assert(again.Lifecycle().Startup == StartupKind.Recovery && again.Lifecycle().LastShutdown!.Dirty.Contains("node:doomed"));
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// #1301 (octahedron-1191 2026-10-21 08:35): the drain's deadline passed while local-node-1 was still Closed, the
    /// request failed, and the node crashed 2 s later. Nobody completed the shutdown, so the cluster stayed Draining
    /// and an update despite the dirty shutdown (#1296, needs phase Down) was refused. Once no node is Active or Closed,
    /// the lease pass completes it, dirty, naming the dead node; and a WA that died before its final global save does
    /// not leave "global" waitable forever.
    /// </summary>
    private static void TestStrandedShutdown()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-stranded-shutdown-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using (var registry = new GatewayRegistry(directory, Admission, () => now))
            {
                NodeRegistration authority = registry.RegisterNode(Join("wa", "wa", Guid.NewGuid()) with { Role = NodeRole.WA });
                NodeRegistration survivor = Register(registry, "survivor", "survivor", Guid.NewGuid());
                NodeRegistration crashed = Register(registry, "crashed", "crashed", Guid.NewGuid());
                PartitionOwnership kept = registry.ClaimPartition(1, survivor.Node, survivor.Epoch, 0);
                PartitionOwnership lost = registry.ClaimPartition(2, crashed.Node, crashed.Epoch, 0);
                registry.RegisterSave(new SaveId(2, lost.Generation, 1), crashed.Node, crashed.Epoch, SaveKind.Periodic, 1, "periodic", 1, "crashed/periodic");
                Assert(!registry.CompleteStrandedShutdown());   // not draining
                registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                registry.RegisterGlobalSave(new(authority.AuthorityGeneration, 1), authority.Node, authority.Epoch,
                    SaveKind.Final, 1, "final", 1, "wa/final");
                registry.MarkEmpty(authority.Node, authority.Epoch);
                registry.RegisterSave(new SaveId(1, kept.Generation, 1), survivor.Node, survivor.Epoch, SaveKind.Final, 1, "final", 1, "survivor/final");
                registry.ReleasePartition(1, survivor.Node, survivor.Epoch, kept.Generation);
                registry.MarkEmpty(survivor.Node, survivor.Epoch);
                // The deadline: the crashed node is still Closed, so the request fails and nothing may complete it yet.
                Assert(crashed.State == NodeState.Closed && !registry.ShutdownReady());
                AssertConflict(() => registry.CompleteShutdown(false));
                Assert(!registry.CompleteStrandedShutdown() && registry.Lifecycle().Phase == ClusterPhase.Draining);
                now += TimeSpan.FromMinutes(5);
                registry.ExpireLeases();
                Assert(crashed.State == NodeState.Dead && registry.CompleteStrandedShutdown());
                ClusterLifecycle down = registry.Lifecycle();
                Assert(down.Phase == ClusterPhase.Down && down.LastShutdown!.Dirty.SequenceEqual(["node:crashed"])
                    && down.LastShutdown.Manifest.Length == 2 && !registry.CompleteStrandedShutdown());
            }
            using (var registry = new GatewayRegistry(directory, Admission, () => now))
            {
                Assert(registry.Lifecycle() is { Startup: StartupKind.Recovery, LastShutdown.Dirty: ["node:crashed"] });
                // The WA dies in its drain before its final global save, every other node is Empty: "global" can never
                // arrive, so the shutdown is ready and completes dirty instead of waiting it out.
                NodeRegistration authority = registry.RegisterNode(Join("wa", "wa", Guid.NewGuid()) with { Role = NodeRole.WA });
                NodeRegistration node = Register(registry, "node", "node", Guid.NewGuid());
                registry.RegisterGlobalSave(new(authority.AuthorityGeneration, 1), authority.Node, authority.Epoch,
                    SaveKind.Periodic, 1, "periodic", 1, "wa/periodic");
                foreach (ulong partition in new ulong[] { 1, 2 })
                {
                    PartitionOwnership owned = registry.ClaimRecoveries(node.Node, node.Epoch, [partition]).Single().Ownership;
                    registry.RegisterSave(new SaveId(partition, owned.Generation, 1), node.Node, node.Epoch, SaveKind.Final, 1, "final", 1, "node/final");
                    registry.ReleasePartition(partition, node.Node, node.Epoch, owned.Generation);
                }
                registry.BeginShutdown(false, TimeSpan.FromMinutes(1));
                registry.MarkEmpty(node.Node, node.Epoch);
                Assert(!registry.ShutdownReady());
                now += TimeSpan.FromMinutes(5);
                registry.ExpireLeases();
                Assert(authority.State == NodeState.Dead && registry.ShutdownReady());
                ClusterLifecycle down = registry.CompleteShutdown(false);
                Assert(down.Phase == ClusterPhase.Down && down.LastShutdown!.Dirty.Contains("global")
                    && down.LastShutdown.Dirty.Contains("node:wa"));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestHandoverFinalizationLease()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-handover-lease-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(3));
            NodeRegistration source = Register(registry, "source", "source", Guid.NewGuid());
            NodeRegistration target = Register(registry, "target", "target", Guid.NewGuid());
            PartitionOwnership owner = registry.ClaimPartition(1, source.Node, source.Epoch, 0);
            registry.Begin("handover", 1, source.Node, source.Epoch, owner.Generation, target.Node, target.Epoch);
            registry.Activate("handover");
            HandoverRecord committed = registry.Commit("handover");
            DateTimeOffset finalizationExpiry = now + GatewayRegistry.HandoverFinalizationLease;
            Assert(registry.GetPartitionOwner(1).LeaseExpires == finalizationExpiry && target.LeaseExpires == finalizationExpiry);

            registry.Heartbeat(target.Node, target.Epoch, []);
            Assert(target.LeaseExpires == finalizationExpiry);

            NodeRegistration reportedTarget = Register(registry, "reported-target", "reported-target", Guid.NewGuid());
            PartitionOwnership secondOwner = registry.ClaimPartition(2, source.Node, source.Epoch, 0);
            registry.Begin("reported", 2, source.Node, source.Epoch, secondOwner.Generation, reportedTarget.Node, reportedTarget.Epoch);
            registry.Activate("reported");
            HandoverRecord reported = registry.Commit("reported");
            registry.Heartbeat(reportedTarget.Node, reportedTarget.Epoch, [new(2, reported.CommittedGeneration!.Value)]);
            Assert(reportedTarget.LeaseExpires == now + TimeSpan.FromSeconds(3)
                && registry.GetPartitionOwner(2).LeaseExpires == reportedTarget.LeaseExpires);

            now += TimeSpan.FromSeconds(3);
            Assert(registry.ExpireLeases() == 1 && registry.GetPartitionOwner(1).Node == target.Node);
            now = finalizationExpiry - TimeSpan.FromMilliseconds(1);
            Assert(registry.ExpireLeases() == 0 && registry.GetPartitionOwner(1).Node == target.Node);
            now = finalizationExpiry;
            Assert(registry.ExpireLeases() == 1);
            Assert(registry.GetPartitionOwner(1) is { Node: null, Generation: var generation } && generation == committed.CommittedGeneration!.Value + 1);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestRestartReconciliationDecision()
    {
        var begun = new HandoverRecord("h", 1, "source", 1, 4, "target", 1, HandoverState.Begun, null);
        var committed = new HandoverRecord("h", 1, "source", 1, 4, "target", 1, HandoverState.Committed, 5);
        var missing = new DirectGateway.TransferProbe(true, null);
        var unreachable = new DirectGateway.TransferProbe(false, null);
        var source = new DirectGateway.TransferProbe(true, new(true, false, false, false, false, 0, false, false, false));
        var target = new DirectGateway.TransferProbe(true, new(false, true, true, false, true, 0, false, false, false));

        Assert(DirectGateway.DecideReconciliation(begun, unreachable, missing, false, false, true, false) == DirectGateway.ReconciliationAction.Wait);
        Assert(DirectGateway.DecideReconciliation(begun, unreachable, missing, false, false, false, false) == DirectGateway.ReconciliationAction.Recover);
        Assert(DirectGateway.DecideReconciliation(begun, source, target, false, false, true, true) == DirectGateway.ReconciliationAction.Abort);
        Assert(DirectGateway.DecideReconciliation(begun, missing, target, false, false, false, true) == DirectGateway.ReconciliationAction.Complete);
        Assert(DirectGateway.DecideReconciliation(begun, missing, missing, false, false, false, false) == DirectGateway.ReconciliationAction.Recover);
        // T-0296 round 2: the target owns the committed generation and does not know the transfer (swept) - complete, never
        // release its live row (handover-reconcile-committed-pruned); without the live ownership it is still Recover.
        Assert(DirectGateway.DecideReconciliation(committed, missing, missing, false, true, false, true) == DirectGateway.ReconciliationAction.Complete);
        Assert(DirectGateway.DecideReconciliation(committed, missing, missing, false, false, false, true) == DirectGateway.ReconciliationAction.Recover);
        Assert(DirectGateway.DecideReconciliation(committed, missing, missing, false, false, false, false) == DirectGateway.ReconciliationAction.Recover);
    }

    private static void TestMultiplexing() => RunMultiplexing(gatewayRs: null);

    /// <summary>
    /// T-0229: the same scenario through <c>--relay external</c> and the gateway-rs binary named by
    /// GATEWAY_RS_BIN - C# LiteNetLib clients and nodes against the Rust data plane. Without the
    /// variable the section says so and passes; CI sets it.
    /// </summary>
    private static void TestMultiplexingExternal()
    {
        string? binary = Environment.GetEnvironmentVariable("GATEWAY_RS_BIN");
        if (string.IsNullOrEmpty(binary))
        {
            Console.WriteLine("self-test section multiplexing-external: SKIPPED (GATEWAY_RS_BIN is not set)");
            return;
        }
        Assert(File.Exists(binary));
        RunMultiplexing(binary);
    }

    /// <summary>
    /// gateway-rs's end of the relay socket, scripted: says Hello, heartbeats, answers EnsureLink with
    /// a connected LinkState, and reads the messages the Gateway sends.
    /// </summary>
    private sealed class ScriptedRelay : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.Unix, (SocketType)5, ProtocolType.Unspecified) { ReceiveTimeout = 5000 };
        private readonly object _sendLock = new();
        private readonly Timer _heartbeats;
        /// <summary>Every JSON message read so far, wanted or not (a test clears it at the point it cares from).</summary>
        public readonly List<RelayIpcMessage> Seen = [];
        /// <summary>T-0369: called for every message read, wanted or not (a script that answers the Gateway).</summary>
        public Action<RelayIpcMessage>? OnMessage;

        public ScriptedRelay(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { _socket.Connect(new UnixDomainSocketEndPoint(path)); break; }
                catch (SocketException) when (attempt < 100) { Thread.Sleep(50); }
            }
            _heartbeats = new Timer(_ => { try { Send(RelayIpcType.Heartbeat, 0, new { seq = 1 }); } catch { } }, null, 0, 300);
            Assert(Read(RelayIpcType.Hello).Json.GetProperty("role").GetString() == "registry");
            Send(RelayIpcType.Hello, 0, new { version = RelayIpcServer.Version, role = "relay", pid = 1, build = "scripted" });
        }

        public void Send(ushort type, uint correlation, object body)
        {
            byte[] json = body as byte[] ?? System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body);
            byte[] frame = new byte[6 + json.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(frame, type);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2), correlation);
            json.CopyTo(frame, 6);
            lock (_sendLock) _socket.Send(frame);
        }

        public RelayIpcMessage Read(ushort wanted, Func<RelayIpcMessage, bool>? match = null)
        {
            byte[] buffer = new byte[RelayIpcServer.MaximumMessage];
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(10))
            {
                int read = _socket.Receive(buffer);
                var message = new RelayIpcMessage(BinaryPrimitives.ReadUInt16LittleEndian(buffer),
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(2)), buffer.AsSpan(6, read - 6).ToArray());
                if (message.Type is not (RelayIpcType.Heartbeat or RelayIpcType.NodeGlobal or RelayIpcType.NodePlugin or RelayIpcType.ClientSend)) Seen.Add(message);
                if (message.Type == RelayIpcType.EnsureLink)
                    Send(RelayIpcType.LinkState, 0, new { endpoint = message.Json.GetProperty("endpoint").GetString(), connected = true });
                OnMessage?.Invoke(message);
                if (message.Type == wanted && (match == null || match(message)))
                    return message;
            }
            throw new InvalidOperationException($"self-test: no relay message {wanted}");
        }

        public void Dispose()
        {
            _heartbeats.Dispose();
            _socket.Dispose();
        }
    }

    /// <summary>
    /// gateway-rs asks for joins concurrently (each JoinRequest is handled off the IPC thread), and
    /// the member limit must still hold: the in-process relay admits on one poll thread. Before the
    /// join lock, 74 bench clients against a limit of 16 admitted 32.
    /// </summary>
    /// <summary>
    /// T-0411: HttpListener answers a bodyless POST or PUT (no Content-Length, no Transfer-Encoding) with 411 itself and still
    /// hands the context over; the gateway must then run nothing. Against a real control listener: a bodyless POST of a
    /// cost-model override gets 411 and the model is unchanged; the same POST with Content-Length: 0 gets 200 and applies (the
    /// known positive); a bodyless PUT gets 411 too.
    /// </summary>
    private static void TestControlBodylessPost()
    {
        string directory = Directory.CreateTempSubdirectory("control-411-selftest-").FullName;
        string path = Path.Combine(directory, "gateway-relay.sock");
        int controlPort = FreeTcpPort();
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, FreeUdpPort()),
            new(IPAddress.Loopback, controlPort), false, null, null, null, null, TimeSpan.FromSeconds(10), null, null, [],
            ExternalRelay: true, RelaySocket: path);
        using var gateway = new DirectGateway(options);
        var gatewayThread = new Thread(() =>
        {
            try { gateway.Run(); }
            catch (Exception e) { Console.Error.WriteLine("control-bodyless-post gateway failed: " + e); }
        }) { IsBackground = true, Name = "Gateway-Control-411-Self-Test" };
        gatewayThread.Start();
        using var relay = new ScriptedRelay(path);
        relay.Read(RelayIpcType.Config);
        using var http = new HttpClient();
        double Drop()
        {
            using System.Text.Json.JsonDocument model = System.Text.Json.JsonDocument.Parse(
                http.GetStringAsync($"http://127.0.0.1:{controlPort}/balance/cost-model").GetAwaiter().GetResult());
            return model.RootElement.GetProperty("BalanceMinPeakDrop").GetDouble();
        }
        // A raw request, so exactly the headers named here are sent (HttpClient always adds Content-Length to a POST).
        int Raw(string verb, string target, string extraHeaders)
        {
            using var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", controlPort);
            using var stream = tcp.GetStream();
            byte[] request = System.Text.Encoding.ASCII.GetBytes($"{verb} {target} HTTP/1.1\r\nHost: 127.0.0.1:{controlPort}\r\nConnection: close\r\n{extraHeaders}\r\n");
            stream.Write(request);
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
            string status = reader.ReadLine() ?? "";
            return int.Parse(status.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        double initial = Drop();   // the deployed default (T-0409: 0.01); the refused requests must leave it as it is
        Assert(initial == CostModel.Deployed().BalanceMinPeakDrop);
        int bodyless = Raw("POST", "/balance/cost-model?BalanceMinPeakDrop=0.5", "");
        Thread.Sleep(300);   // the handler ran on the thread pool after the listener's 411, before the fix
        double afterBodyless = Drop();
        int bodylessPut = Raw("PUT", "/balance/cost-model?BalanceMinPeakDrop=0.25", "");
        Thread.Sleep(300);
        double afterPut = Drop();
        int empty = Raw("POST", "/balance/cost-model?BalanceMinPeakDrop=0.5", "Content-Length: 0\r\n");
        double afterEmpty = Drop();
        Console.WriteLine($"control-bodyless-post: bodyless POST {bodyless} -> drop {afterBodyless}; bodyless PUT {bodylessPut} -> {afterPut};"
            + $" POST with Content-Length: 0 {empty} -> {afterEmpty}");
        if (bodyless != 411 || afterBodyless != initial || bodylessPut != 411 || afterPut != initial)
            throw new InvalidOperationException($"self-test failed: a 411-answered control request ran (POST {bodyless} -> {afterBodyless}, PUT {bodylessPut} -> {afterPut})");
        if (empty != 200 || afterEmpty != 0.5)
            throw new InvalidOperationException($"self-test failed: VOID - the known positive (Content-Length: 0) did not apply: {empty} -> {afterEmpty}");
        static bool Answered(string m, string? cl, string? te) => ClusterProtocol.HttpListenerLengthRequired.AlreadyAnswered(m, cl, te);
        Assert(Answered("post", null, null) && Answered("PUT", null, null) && !Answered("POST", "0", null)
            && !Answered("POST", null, "chunked") && !Answered("DELETE", null, null) && !Answered("GET", null, null));
        gateway.Stop();

        // The node control server (Shared/Cluster/ClusterControlServer.Handle) runs the same predicate, and cannot run outside the
        // game. So, on a bare HttpListener: for each verb and body framing, the predicate says "already answered" exactly when
        // the listener itself sent 411 - the predicate is the listener's own behaviour, not a guess at it.
        int barePort = FreeTcpPort();
        using var bare = new HttpListener();
        bare.Prefixes.Add($"http://127.0.0.1:{barePort}/");
        bare.Start();
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(string Target, bool Answered)>();
        var bareThread = new Thread(() =>
        {
            while (bare.IsListening)
            {
                HttpListenerContext c;
                try { c = bare.GetContext(); } catch { return; }
                bool answered = ClusterProtocol.HttpListenerLengthRequired.AlreadyAnswered(c.Request.HttpMethod,
                    c.Request.Headers["Content-Length"], c.Request.Headers["Transfer-Encoding"]);
                seen.Enqueue((c.Request.Url!.PathAndQuery, answered));
                try { c.Response.StatusCode = 200; c.Response.ContentLength64 = 0; c.Response.Close(); } catch (ObjectDisposedException) { }
            }
        }) { IsBackground = true, Name = "Control-411-Bare-Listener" };
        bareThread.Start();
        (string Verb, string Headers)[] cases =
        [
            ("POST", ""), ("PUT", ""), ("POST", "Content-Length: 0\r\n"), ("PUT", "Content-Length: 0\r\n"),
            ("POST", "Transfer-Encoding: chunked\r\n"), ("DELETE", ""), ("PATCH", ""), ("GET", ""),
        ];
        var mismatches = new List<string>();
        for (int i = 0; i < cases.Length; i++)
        {
            using var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", barePort);
            using var stream = tcp.GetStream();
            string body = cases[i].Headers.StartsWith("Transfer-Encoding", StringComparison.Ordinal) ? "0\r\n\r\n" : "";
            stream.Write(System.Text.Encoding.ASCII.GetBytes($"{cases[i].Verb} /case{i} HTTP/1.1\r\nHost: 127.0.0.1:{barePort}\r\n"
                + $"Connection: close\r\n{cases[i].Headers}\r\n{body}"));
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
            int status = int.Parse((reader.ReadLine() ?? "x 0").Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            (string Target, bool Answered) got = default;
            for (int wait = 0; wait < 50 && !seen.TryDequeue(out got); wait++) Thread.Sleep(20);
            if (got.Target != $"/case{i}" || got.Answered != (status == 411))
                mismatches.Add($"{cases[i].Verb} [{cases[i].Headers.Trim()}]: status {status}, predicate {got.Answered} ({got.Target})");
        }
        bare.Stop();
        Console.WriteLine($"control-bodyless-post: bare listener, {cases.Length} verb/framing cases, predicate == (status 411): "
            + (mismatches.Count == 0 ? "all" : string.Join("; ", mismatches)));
        if (mismatches.Count != 0) throw new InvalidOperationException("self-test failed: the 411 predicate disagrees with the listener: " + string.Join("; ", mismatches));
    }

    private static void TestRelayJoinAdmission()
    {
        string directory = Directory.CreateTempSubdirectory("relay-join-selftest-").FullName;
        string path = Path.Combine(directory, "gateway-relay.sock");
        Environment.SetEnvironmentVariable("CLUSTER_ALLOW_TEST_ACCEPT_ALL", "true");
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, FreeUdpPort()), null,
            false, null, null, null, null, TimeSpan.FromSeconds(10), null, null, [], DirectFrontend: true,
            ExternalRelay: true, RelaySocket: path);
        using var gateway = new DirectGateway(options);
        var gatewayThread = new Thread(() =>
        {
            try { gateway.Run(); }
            catch (Exception e) { Console.Error.WriteLine("relay-join-admission gateway failed: " + e); }
        }) { IsBackground = true, Name = "Gateway-Relay-Join-Self-Test" };
        gatewayThread.Start();
        using var relay = new ScriptedRelay(path);
        relay.Read(RelayIpcType.Config);
        const int joins = 64, limit = 16;
        for (int i = 0; i < joins; i++)
            relay.Send(RelayIpcType.JoinRequest, (uint)(0x100 + i), new { clientId = 5000UL + (ulong)i, session = 1UL + (ulong)i, service = "DirectTransport", name = "j" + i, remote = "127.0.0.1:1" });
        int accepted = 0, rejected = 0;
        while (accepted + rejected < joins)
        {
            string? decision = relay.Read(RelayIpcType.JoinAnswer).Json.GetProperty("decision").GetString();
            if (decision == "accept") accepted++;
            else rejected++;
        }
        Console.WriteLine($"relay-join-admission: {accepted} accepted, {rejected} refused (limit {limit})");
        Assert(accepted == limit);
        gateway.Stop();
        Environment.SetEnvironmentVariable("CLUSTER_ALLOW_TEST_ACCEPT_ALL", null);
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// T-0229: the C# half of a Steam session terminated by gateway-rs, against a scripted relay
    /// peer: the join, the ticket through SteamAuth/SteamEvent, admission and attach, a perimeter
    /// rejection, a mid-session revocation, the browser row and the Steam status.
    /// </summary>
    private static void TestSteamExternal()
    {
        string directory = Directory.CreateTempSubdirectory("relay-steam-selftest-").FullName;
        string path = Path.Combine(directory, "gateway-relay.sock");
        int controlPort = FreeTcpPort();
        var backend = new IPEndPoint(IPAddress.Loopback, FreeUdpPort());
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), backend, new(IPAddress.Loopback, controlPort),
            false, null, null, null, null, TimeSpan.FromSeconds(10), null, null, [],
            SteamListen: new(IPAddress.Loopback, FreeUdpPort()), ExternalRelay: true, RelaySocket: path);
        using var gateway = new DirectGateway(options);
        var gatewayThread = new Thread(() =>
        {
            try { gateway.Run(); }
            catch (Exception e) { Console.Error.WriteLine("steam-external gateway failed: " + e); }
        }) { IsBackground = true, Name = "Gateway-Steam-External-Self-Test" };
        gatewayThread.Start();

        using var scripted = new ScriptedRelay(path);
        void Send(ushort type, uint correlation, object body) => scripted.Send(type, correlation, body);
        RelayIpcMessage Read(ushort wanted, Func<RelayIpcMessage, bool>? match = null) => scripted.Read(wanted, match);
        System.Text.Json.JsonElement config = Read(RelayIpcType.Config).Json;
        Assert(config.GetProperty("steam").GetProperty("enabled").GetBoolean() && !config.GetProperty("direct").GetProperty("enabled").GetBoolean());
        System.Text.Json.JsonElement publish = Read(RelayIpcType.SteamPublish).Json;
        Assert(publish.GetProperty("players").GetArrayLength() == 0 && publish.GetProperty("maxPlayers").GetInt32() > 0);

        const ulong user = 76561198223054696, rejected = 76561198000000002;
        // A Steam session asks to join and is accepted into TicketPending; nothing reaches a node.
        void Join(ulong clientId, ulong session, uint correlation)
        {
            Send(RelayIpcType.JoinRequest, correlation, new { clientId, session, service = "Steam", name = (string?)null, remote = $"steam:{clientId}" });
            RelayIpcMessage answer = Read(RelayIpcType.JoinAnswer);
            Assert(answer.Correlation == correlation && answer.Json.GetProperty("decision").GetString() == "accept");
            Send(RelayIpcType.ClientMessage, 0, new { clientId, session, messageId = 14, channel = 2, packet = Convert.ToBase64String(ClientConnectedFixture) });
            System.Text.Json.JsonElement auth = Read(RelayIpcType.SteamAuth).Json;
            Assert(auth.GetProperty("op").GetString() == "begin" && auth.GetProperty("clientId").GetUInt64() == clientId);
            byte[] ticket = Convert.FromBase64String(auth.GetProperty("ticket").GetString()!);
            Assert(ticket.Length == 234 && ticket[1] == 10);
        }
        bool For(RelayIpcMessage message, ulong clientId) => message.Type == RelayIpcType.ClientSend
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(message.Body.Span) == clientId
            : message.Json.GetProperty("clientId").GetUInt64() == clientId;

        Join(user, 11, 0x101);
        Send(RelayIpcType.SteamEvent, 0, new { kind = "ticket", clientId = user, response = 0, owner = user });
        RelayIpcMessage attach = Read(RelayIpcType.NodeFrame, m => m.Json.GetProperty("kind").GetString() == "attach" && For(m, user));
        Assert(attach.Json.GetProperty("endpoint").GetString() == backend.ToString());
        RelayIpcMessage listed = Read(RelayIpcType.SteamPublish, m => m.Json.GetProperty("players").GetArrayLength() == 1);
        Assert(listed.Json.GetProperty("players")[0].GetProperty("userId").GetUInt64() == user
            && listed.Json.GetProperty("players")[0].GetProperty("name").GetString() == "Viktor");

        // A ticket Steam refuses: JOIN_RESULT to the client, then the disconnect; no node sees it.
        Join(rejected, 12, 0x201);
        Send(RelayIpcType.SteamEvent, 0, new { kind = "ticketRejected", clientId = rejected, response = 1 });
        RelayIpcMessage result = Read(RelayIpcType.ClientSend, m => For(m, rejected));
        Assert(result.Body.Span[16] == 2 && result.Body.Span[18 + 8] == VanillaJoinCodec.JoinResultMessageId);
        Assert(Read(RelayIpcType.Disconnect, m => For(m, rejected)).Json.GetProperty("session").GetUInt64() == 12);

        // A later verdict revokes the admitted session: the reconnect control message, then the disconnect.
        Send(RelayIpcType.SteamEvent, 0, new { kind = "ticket", clientId = user, response = 6, owner = user });
        RelayIpcMessage control = Read(RelayIpcType.ClientSend, m => For(m, user) && m.Body.Span[16] == 0);
        Assert(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(control.Body.Span[18..]) == 1);
        Assert(Read(RelayIpcType.Disconnect, m => For(m, user)).Json.GetProperty("session").GetUInt64() == 11);
        Send(RelayIpcType.ClientEvent, 0, new { clientId = user, session = 11UL, @event = "disconnected", detail = "steam-session-closed" });
        Assert(Read(RelayIpcType.SteamPublish, m => m.Json.GetProperty("players").GetArrayLength() == 0).Type == RelayIpcType.SteamPublish);

        // /transport/status shows gateway-rs's Steam section.
        using var http = new HttpClient();
        Task<string> status = http.GetStringAsync($"http://127.0.0.1:{controlPort}/transport/status");
        RelayIpcMessage asked = Read(RelayIpcType.Status, m => !m.Json.TryGetProperty("purpose", out _));
        Send(RelayIpcType.Status, asked.Correlation, new { sessions = Array.Empty<object>(), steam = new { serverId = 90293318619908126UL, loggedOn = true } });
        Assert(status.Wait(10000) && status.Result.Contains("90293318619908126", StringComparison.Ordinal));
        gateway.Stop();
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// T-0244 (w33, 2026-09-25): a Steam player whose node died looped for minutes. The forced
    /// reconnect named the Direct Transport server id, which a vanilla client does not know, so it
    /// kept playing; its traffic reopened the Steam session with no login, the 10 s backlog age
    /// force-reconnected that, and so on every 10 s. Now the reconnect names the Steam GameServer,
    /// a session without a login is ended (refresh) and, past the bound, the client is booted to
    /// the main menu with a JOIN_RESULT; a real rejoin then ends the streak.
    /// </summary>
    private static void TestSteamReopenWithoutLogin() => SteamReopenWithoutLogin(registryDead: false);

    /// <summary>T-0244 (2026-09-27 kill test): the registry's Dead decision ends the session, no link timeout needed.</summary>
    private static void TestSteamNodeDead() => SteamReopenWithoutLogin(registryDead: true);

    /// <summary>
    /// T-0237 step 3: a Steam join whose route is "come back shortly" (the home partition offline and
    /// loading) is resolved again at admission instead of landing on the default backend.
    /// </summary>
    /// <summary>
    /// T-0298: a Steam join (gateway-rs) whose character home is recovering is HELD over the relay IPC, not
    /// refused. The refusal failed every player's first login after a restart (all homes offline).
    /// </summary>
    private static void TestSteamHomeRecoveryHold()
    {
        string directory = Directory.CreateTempSubdirectory("relay-steam-home-hold-selftest-").FullName;
        string registryDirectory = Path.Combine(directory, "registry");
        const ulong user = 76561198223054696;
        using (var registry = new GatewayRegistry(registryDirectory, Admission, leaseTtl: TimeSpan.FromMinutes(10)))
        {
            NodeRegistration node = Register(registry, "first", "127.0.0.1:1", Guid.NewGuid());
            PartitionOwnership owner = registry.ClaimPartition(1, node.Node, node.Epoch, 0);
            ReportCharacterHome(registry, user, 7, 900, 1, "first");
            registry.RegisterSave(new SaveId(1, owner.Generation, 1), node.Node, node.Epoch, SaveKind.Final, 1, "drain", 1, "one/final");
            registry.ReleasePartition(1, node.Node, node.Epoch, owner.Generation);
            Assert(registry.PendingHomeRecovery(user));
            registry.Checkpoint();
        }
        string path = Path.Combine(directory, "gateway-relay.sock");
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), new(IPAddress.Loopback, FreeUdpPort()), null,
            false, null, null, null, registryDirectory, TimeSpan.FromMinutes(10), Admission, null, [],
            SteamListen: new(IPAddress.Loopback, FreeUdpPort()), ExternalRelay: true, RelaySocket: path);
        using var gateway = new DirectGateway(options);
        try
        {
            var gatewayThread = new Thread(() =>
            {
                try { gateway.Run(); }
                catch (Exception e) { Console.Error.WriteLine("steam-home-recovery-hold gateway failed: " + e); }
            }) { IsBackground = true, Name = "Gateway-Steam-Home-Hold-Self-Test" };
            gatewayThread.Start();
            using var relay = new ScriptedRelay(path);
            relay.Read(RelayIpcType.Config);
            for (uint ask = 0; ask < 2; ask++)   // the first ask and one gateway-rs re-ask
            {
                relay.Send(RelayIpcType.JoinRequest, 0x500 + ask, new { clientId = user, session = 9UL, service = "Steam", name = (string?)null, remote = $"steam:{user}" });
                string? decision = relay.Read(RelayIpcType.JoinAnswer, m => m.Correlation == 0x500 + ask).Json.GetProperty("decision").GetString();
                Console.WriteLine($"steam-home-recovery-hold: ask {ask} answered {decision}");
                Assert(decision == "hold");
            }
            gateway.Stop();
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } }
    }

    /// <summary>
    /// T-0357 (2026-10-01 23:44:39): a Steam join admitted and bound while the gateway was still --paused for
    /// the boot was not attached, nothing retried, and gateway-rs's 10 s backlog age force-reconnected it two
    /// seconds before the /resume. Against a scripted relay and a scripted World Authority: the binding is
    /// granted under the pause, the backlog age is reported and must NOT end the session, the resume attaches
    /// it and the node's acknowledgement makes it ready. Then the bound: a second join whose node never
    /// acknowledges is not ended by the backlog age either, but by the attach deadline, with its own reason.
    /// </summary>
    private static void TestSteamAdmittedWhilePaused()
    {
        string directory = Directory.CreateTempSubdirectory("relay-steam-paused-selftest-").FullName;
        string registryDirectory = Path.Combine(directory, "registry");
        const ulong user = 76561198223054696, second = 76561198000000003;
        string waEndpoint = "127.0.0.1:" + FreeUdpPort(), nodeEndpoint = "127.0.0.1:" + FreeUdpPort();
        long generation;
        using (var registry = new GatewayRegistry(registryDirectory, Admission, leaseTtl: TimeSpan.FromMinutes(10)))
        {
            generation = registry.RegisterNode(Join("wa", waEndpoint, Guid.NewGuid()) with { Role = NodeRole.WA }).AuthorityGeneration;
            Register(registry, "first", nodeEndpoint, Guid.NewGuid());
            registry.Checkpoint();
        }
        string path = Path.Combine(directory, "gateway-relay.sock");
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), IPEndPoint.Parse(nodeEndpoint), null,
            true, null, null, null, registryDirectory, TimeSpan.FromMinutes(10), Admission, null, [],
            SteamListen: new(IPAddress.Loopback, FreeUdpPort()), ExternalRelay: true, RelaySocket: path);
        TimeSpan attachTimeout = DirectGateway.AttachRetryTimeout;
        DirectGateway.AttachRetryTimeout = TimeSpan.FromSeconds(3);
        using var gateway = new DirectGateway(options);
        try
        {
            var gatewayThread = new Thread(() =>
            {
                try { gateway.Run(); }
                catch (Exception e) { Console.Error.WriteLine("steam-admitted-while-paused gateway failed: " + e); }
            }) { IsBackground = true, Name = "Gateway-Steam-Paused-Self-Test" };
            gatewayThread.Start();
            using var relay = new ScriptedRelay(path);
            relay.Read(RelayIpcType.Config);
            Assert(relay.Read(RelayIpcType.Paused).Json.GetProperty("paused").GetBoolean());

            bool For(RelayIpcMessage message, ulong clientId) => message.Json.GetProperty("clientId").GetUInt64() == clientId;
            bool Flag(RelayIpcMessage message, string name) => message.Json.TryGetProperty(name, out System.Text.Json.JsonElement value)
                && value.ValueKind == System.Text.Json.JsonValueKind.True;
            // The join, its login and the ticket verdict, then the World Authority's answer to the players command.
            void Admit(ulong clientId, ulong session, uint correlation)
            {
                relay.Send(RelayIpcType.JoinRequest, correlation, new { clientId, session, service = "Steam", name = (string?)null, remote = $"steam:{clientId}" });
                Assert(relay.Read(RelayIpcType.JoinAnswer, m => m.Correlation == correlation).Json.GetProperty("decision").GetString() == "accept");
                relay.Send(RelayIpcType.ClientMessage, 0, new { clientId, session, messageId = 14, channel = 2, packet = Convert.ToBase64String(ClientConnectedFixture) });
                Assert(relay.Read(RelayIpcType.SteamAuth, m => For(m, clientId)).Json.GetProperty("op").GetString() == "begin");
                relay.Send(RelayIpcType.SteamEvent, 0, new { kind = "ticket", clientId, response = 0, owner = clientId });
                ReadOnlySpan<byte> command = relay.Read(RelayIpcType.NodeGlobal).Body.Span;
                int endpointLength = BinaryPrimitives.ReadUInt16LittleEndian(command[1..]);
                Assert(System.Text.Encoding.UTF8.GetString(command.Slice(3, endpointLength)) == waEndpoint);
                using var envelope = System.Text.Json.JsonDocument.Parse(command[(3 + endpointLength)..].ToArray());
                System.Text.Json.JsonElement root = envelope.RootElement;
                Assert(root.GetProperty("Kind").GetString() == "Command" && root.GetProperty("Subsystem").GetString() == "players");
                using var request = System.Text.Json.JsonDocument.Parse(root.GetProperty("Payload").GetString()!);
                System.Text.Json.JsonElement asked = request.RootElement;
                string ready = System.Text.Json.JsonSerializer.Serialize(new
                {
                    AuthorityGeneration = generation, ServiceName = asked.GetProperty("ServiceName").GetString(),
                    UserId = clientId, OwnerId = asked.GetProperty("OwnerId").GetUInt64(), SerialId = 0,
                    IsAdmin = asked.GetProperty("IsAdmin").GetBoolean(),
                    Identity = new { IdentityId = 4242L + (long)session, Name = "Viktor", Model = "Default_Astronaut", HasColor = false, ColorX = 0f, ColorY = 0f, ColorZ = 0f, IsNpc = false },
                });
                byte[] response = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Kind = "Response", Generation = generation, Subsystem = "players", Sequence = root.GetProperty("Sequence").GetInt64(),
                    Origin = "wa", Target = "$gateway",
                    Payload = System.Text.Json.JsonSerializer.Serialize(new { Success = true, Payload = ready, Error = "" }),
                    OperationId = root.GetProperty("OperationId").GetGuid(),
                });
                byte[] endpoint = System.Text.Encoding.UTF8.GetBytes(waEndpoint);
                byte[] tunnel = new byte[3 + endpoint.Length + response.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(tunnel.AsSpan(1), (ushort)endpoint.Length);
                endpoint.CopyTo(tunnel, 3);
                response.CopyTo(tunnel, 3 + endpoint.Length);
                relay.Send(RelayIpcType.NodeGlobal, 0, tunnel);
            }
            bool Attach(RelayIpcMessage message, ulong clientId) => message.Json.GetProperty("kind").GetString() == "attach" && For(message, clientId);

            // Bound under the pause: gateway-rs is told the attach is pending, and no Attach is sent.
            Admit(user, 21, 0x601);
            relay.Read(RelayIpcType.SessionUpdate, m => For(m, user) && Flag(m, "attachPending"));
            // An older gateway-rs (or the age running out the instant the hold ends) still reports the age.
            relay.Send(RelayIpcType.ClientEvent, 0, new { clientId = user, session = 21UL, @event = "backlogExpired", detail = "" });
            Thread.Sleep(500);
            typeof(DirectGateway).GetMethod("Resume", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(gateway, null);
            relay.Seen.Clear();
            RelayIpcMessage attach;
            try { attach = relay.Read(RelayIpcType.NodeFrame, m => Attach(m, user)); }
            catch (InvalidOperationException)
            {
                bool ended = relay.Seen.Any(m => m.Type == RelayIpcType.Disconnect && For(m, user));
                Console.WriteLine("steam-admitted-while-paused: FAILED: no Attach after the resume"
                    + (ended ? "; the session was force-reconnected on the backlog age under the pause" : ""));
                throw;
            }
            Assert(attach.Json.GetProperty("endpoint").GetString() == nodeEndpoint);
            Assert(!relay.Seen.Any(m => m.Type == RelayIpcType.Disconnect && For(m, user)));
            relay.Send(RelayIpcType.NodeControl, 0, new { clientId = user, session = 21UL, endpoint = nodeEndpoint, kind = "attachAck" });
            relay.Read(RelayIpcType.SessionUpdate, m => For(m, user) && Flag(m, "attached") && !Flag(m, "attachPending"));
            Console.WriteLine("steam-admitted-while-paused: bound under the pause, the backlog age ignored, attached by the resume, ready");

            // The bound: a node that never acknowledges. The backlog age does not end the session, the attach deadline does.
            Admit(second, 22, 0x602);
            relay.Read(RelayIpcType.NodeFrame, m => Attach(m, second));
            Stopwatch waited = Stopwatch.StartNew();
            relay.Send(RelayIpcType.ClientEvent, 0, new { clientId = second, session = 22UL, @event = "backlogExpired", detail = "" });
            Assert(relay.Read(RelayIpcType.Disconnect, m => For(m, second)).Json.GetProperty("session").GetUInt64() == 22);
            Console.WriteLine($"steam-admitted-while-paused: a never-acknowledged attach ended the session after {waited.Elapsed.TotalSeconds:0.0}s (deadline 3 s)");
            Assert(waited.Elapsed >= TimeSpan.FromSeconds(2.5) && waited.Elapsed < TimeSpan.FromSeconds(8));
            gateway.Stop();
        }
        finally
        {
            DirectGateway.AttachRetryTimeout = attachTimeout;
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static void TestSteamDeferredRoute()
    {
        var home = new ClientRoute(7, 72295, "node-3", 33, 2, "127.0.0.1:28419");
        TimeSpan step = TimeSpan.FromMilliseconds(20);
        int calls = 0;
        ClientRoute? LoadsOnThird()
        {
            if (++calls < 3) throw new RegistryBusyException($"partition 72295 is offline and loading (call {calls})");
            return home;
        }
        var (route, last, gaveUp) = DirectGateway.WaitForRoute(LoadsOnThird, "offline and loading", TimeSpan.FromSeconds(5), step, () => false).Result;
        Console.WriteLine($"steam-deferred-route: loaded on call {calls}: {route?.Node} (last: {last})");
        Assert(route == home && !gaveUp && calls == 3);

        calls = 0;
        (route, last, gaveUp) = DirectGateway.WaitForRoute(() => { calls++; throw new RegistryBusyException("still loading"); },
            null, TimeSpan.FromMilliseconds(200), step, () => false).Result;
        Console.WriteLine($"steam-deferred-route: never routable: gave up after {calls} calls (last: {last})");
        Assert(route == null && gaveUp && last == "still loading" && calls >= 3);

        calls = 0;
        (route, last, gaveUp) = DirectGateway.WaitForRoute(() => { calls++; throw new RegistryConflictException("not known"); },
            null, TimeSpan.FromSeconds(5), step, () => false).Result;
        Assert(route == null && !gaveUp && last == "not known" && calls == 1);

        calls = 0;
        (route, _, gaveUp) = DirectGateway.WaitForRoute(() => { calls++; throw new RegistryBusyException("loading"); },
            null, TimeSpan.FromSeconds(5), step, () => calls >= 2).Result;
        Assert(route == null && gaveUp && calls == 2);

        (route, _, gaveUp) = DirectGateway.WaitForRoute(() => null, "loading", TimeSpan.FromSeconds(5), step, () => false).Result;
        Assert(route == null && !gaveUp);
    }

    private static void SteamReopenWithoutLogin(bool registryDead)
    {
        string directory = Directory.CreateTempSubdirectory("relay-steam-reopen-selftest-").FullName;
        string path = Path.Combine(directory, "gateway-relay.sock");
        int controlPort = FreeTcpPort();
        var backend = new IPEndPoint(IPAddress.Loopback, FreeUdpPort());
        var options = new GatewayOptions(new(IPAddress.Loopback, FreeUdpPort()), backend, new(IPAddress.Loopback, controlPort),
            false, null, null, null, null, TimeSpan.FromSeconds(10), null, null, [],
            SteamListen: new(IPAddress.Loopback, FreeUdpPort()), ExternalRelay: true, RelaySocket: path);
        TimeSpan grace = DirectGateway.SteamLoginGrace, bound = DirectGateway.SteamRefreshBound;
        DirectGateway.SteamLoginGrace = TimeSpan.FromSeconds(1);
        DirectGateway.SteamRefreshBound = TimeSpan.FromSeconds(3);
        using var gateway = new DirectGateway(options);
        try
        {
            var gatewayThread = new Thread(() =>
            {
                try { gateway.Run(); }
                catch (Exception e) { Console.Error.WriteLine("steam-reopen gateway failed: " + e); }
            }) { IsBackground = true, Name = "Gateway-Steam-Reopen-Self-Test" };
            gatewayThread.Start();

            using var scripted = new ScriptedRelay(path);
            void Send(ushort type, uint correlation, object body) => scripted.Send(type, correlation, body);
            RelayIpcMessage Read(ushort wanted, Func<RelayIpcMessage, bool>? match = null) => scripted.Read(wanted, match);
            const ulong user = 76561198223054696, steamServer = 90293439961095198;
            bool For(RelayIpcMessage message) => message.Type == RelayIpcType.ClientSend
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(message.Body.Span) == user
                : message.Json.GetProperty("clientId").GetUInt64() == user;
            ulong Session(RelayIpcMessage message) => message.Type == RelayIpcType.ClientSend
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(message.Body.Span[8..])
                : message.Json.GetProperty("session").GetUInt64();
            Read(RelayIpcType.Config);
            // The Gateway learns the GameServer id from gateway-rs's Status.
            RelayIpcMessage poll = Read(RelayIpcType.Status, m => m.Json.TryGetProperty("purpose", out _));
            Send(RelayIpcType.Status, poll.Correlation, new { sessions = Array.Empty<object>(), steam = new { serverId = steamServer, loggedOn = true } });

            uint correlation = 0x400;
            void Open(ulong session)
            {
                Send(RelayIpcType.JoinRequest, ++correlation, new { clientId = user, session, service = "Steam", name = (string?)null, remote = $"steam:{user}" });
                RelayIpcMessage answer = Read(RelayIpcType.JoinAnswer, m => m.Correlation == correlation);
                Assert(answer.Json.GetProperty("decision").GetString() == "accept");
            }
            void PresentLogin(ulong session)
            {
                Send(RelayIpcType.ClientMessage, 0, new { clientId = user, session, messageId = 14, channel = 2, packet = Convert.ToBase64String(ClientConnectedFixture) });
                Assert(Read(RelayIpcType.SteamAuth, m => m.Json.GetProperty("op").GetString() == "begin").Json.GetProperty("clientId").GetUInt64() == user);
            }
            void Login(ulong session, TimeSpan verdictAfter = default)
            {
                PresentLogin(session);
                Thread.Sleep(verdictAfter);
                Send(RelayIpcType.SteamEvent, 0, new { kind = "ticket", clientId = user, response = 0, owner = user });
                Read(RelayIpcType.NodeFrame, m => m.Json.GetProperty("kind").GetString() == "attach" && For(m));
            }
            // The ending of one session: its first message to the client, then the disconnect, then
            // gateway-rs reports the session closed.
            RelayIpcMessage Ended(ulong session)
            {
                RelayIpcMessage first = Read(RelayIpcType.ClientSend, m => For(m) && Session(m) == session);
                Assert(Read(RelayIpcType.Disconnect, m => For(m)).Json.GetProperty("session").GetUInt64() == session);
                Send(RelayIpcType.ClientEvent, 0, new { clientId = user, session, @event = "disconnected", detail = "steam-session-closed" });
                return first;
            }
            void AssertHostLeft(RelayIpcMessage control, string what)
            {
                ReadOnlySpan<byte> payload = control.Body.Span[18..];
                ulong named = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload[2..]);
                Console.WriteLine($"steam-reopen-without-login: {what}: control message {System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload)} on channel {control.Body.Span[16]} names server {named}");
                Assert(control.Body.Span[16] == 0 && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload) == 1);
                // MyMultiplayerClient_ClientLeft: only the client's ServerId (server.SteamID) is "the host left".
                Assert(named == steamServer);
            }

            Open(21);
            // T-0365: a session that presented its login is not a session without one, however long
            // admission takes (here the ticket verdict arrives past the login grace).
            Login(21, DirectGateway.SteamLoginGrace + TimeSpan.FromMilliseconds(600));
            // The node dies: the forced reconnect must be one the vanilla client acts on.
            Stopwatch sinceForced = Stopwatch.StartNew();
            if (registryDead)
            {
                gateway.OnRegistryNodeDead("node-selftest", backend.ToString());
                AssertHostLeft(Ended(21), "node-dead");
                // gateway-rs's link timeout arrives later and finds the session already ended.
                Send(RelayIpcType.LinkState, 0, new { endpoint = backend.ToString(), connected = false, reason = "selftest link timeout" });
            }
            else
            {
                Send(RelayIpcType.LinkState, 0, new { endpoint = backend.ToString(), connected = false, reason = "selftest node death" });
                AssertHostLeft(Ended(21), "node-link-lost");
            }

            // The client ignores it and keeps playing: each reopened session presents no login. It is
            // refreshed (the host-left message again) until the bound, then booted to the main menu.
            int refreshes = 0;
            RelayIpcMessage ending;
            for (ulong session = 22; ; session++)
            {
                Assert(session < 30);
                Open(session);
                ending = Ended(session);
                if (ending.Body.Span[16] != 0) break;
                AssertHostLeft(ending, "reopened session " + session);
                refreshes++;
            }
            double bootedAfter = sinceForced.Elapsed.TotalSeconds;
            Console.WriteLine($"steam-reopen-without-login: booted {bootedAfter:0.0}s after the forced reconnect, after {refreshes} refresh(es)");
            Assert(refreshes >= 1 && bootedAfter >= 3 && bootedAfter < 3 + 2 * 1 + 2);
            Assert(ending.Body.Span[18..].SequenceEqual(VanillaJoinCodec.EncodeJoinResultFrame(JoinResult.UserNotConnected)));

            using var http = new HttpClient();
            System.Text.Json.JsonElement Status()
            {
                Task<string> status = http.GetStringAsync($"http://127.0.0.1:{controlPort}/transport/status");
                RelayIpcMessage asked = Read(RelayIpcType.Status, m => !m.Json.TryGetProperty("purpose", out _));
                Send(RelayIpcType.Status, asked.Correlation, new { sessions = Array.Empty<object>(), steam = new { serverId = steamServer, loggedOn = true } });
                Assert(status.Wait(10000));
                return System.Text.Json.JsonDocument.Parse(status.Result).RootElement;
            }
            System.Text.Json.JsonElement report = Status();
            System.Text.Json.JsonElement streak = report.GetProperty("reconnectStreaks").EnumerateArray().Single();
            Console.WriteLine($"steam-reopen-without-login: reported {streak}");
            Assert(streak.GetProperty("client").GetUInt64() == user && streak.GetProperty("bootsToMenu").GetInt32() == 1
                && streak.GetProperty("forcedWithoutAdmission").GetInt32() == refreshes + 2);
            Assert(report.GetProperty("counters").GetProperty("steamBootsToMenu").GetInt64() == 1);

            // T-0365: a session that presents a login restarts the refresh clock. This one is lost before
            // its ticket verdict; the login-less session after it is refreshed again (the host-left, which a
            // vanilla client answers with its reconnector), not booted because of an earlier streak's age.
            Open(37);
            PresentLogin(37);
            Send(RelayIpcType.ClientEvent, 0, new { clientId = user, session = 37UL, @event = "disconnected", detail = "steam-session-failed: k_EP2PSessionErrorTimeout" });
            Open(38);
            AssertHostLeft(Ended(38), "login-less session after one with a login");

            // From the menu the player joins again with a login: admitted, and the streak is over.
            Open(40);
            Login(40);
            Assert(Status().GetProperty("reconnectStreaks").GetArrayLength() == 0);

            // T-0248: the cluster shuts down and sends the player to the menu. Vanilla's reconnector retries
            // on the Steam session that is still open: its login must not reach a node (which still lists the
            // player and answers AlreadyJoined, an error box that ends the reconnector). It gets the host-left
            // instead, which makes the client unload and reconnect again.
            gateway.CloseJoiningForSelfTest();
            Send(RelayIpcType.ClientMessage, 0, new { clientId = user, session = 40UL, messageId = 14, channel = 2, packet = Convert.ToBase64String(ClientConnectedFixture) });
            AssertHostLeft(Ended(40), "login while admission is closed");
            gateway.Stop();
        }
        finally
        {
            DirectGateway.SteamLoginGrace = grace;
            DirectGateway.SteamRefreshBound = bound;
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static void RunMultiplexing(string? gatewayRs)
    {
        int gatewayPort = FreeUdpPort();
        int[] nodePorts = new int[2];
        for (int i = 0; i < nodePorts.Length; i++)
        {
            do nodePorts[i] = FreeUdpPort(); while (nodePorts[i] == gatewayPort || nodePorts.Take(i).Contains(nodePorts[i]));
        }
        int controlPort = FreeTcpPort();
        var nodePackets = new ConcurrentQueue<(int Node, NodeLinkPacket Packet)>();
        NetPeer?[] nodePeers = new NetPeer?[2];
        int[] nodeConnections = new int[2];

        NetManager StartNode(int index)
        {
            var listener = new EventBasedNetListener();
            listener.ConnectionRequestEvent += request =>
            {
                if (!DirectTransportCodec.TryDecodeHandshake(request.Data, out ulong id) || id != DirectTransportCodec.NodeLinkId
                    || !DirectTransportCodec.TryDecodeNodeLinkHandshake(request.Data, out _))
                {
                    request.Reject();
                    return;
                }
                nodePeers[index] = request.Accept();
                Interlocked.Increment(ref nodeConnections[index]);
            };
            listener.NetworkReceiveEvent += (peer, reader, _, delivery) =>
            {
                NodeLinkPacket packet = DirectTransportCodec.DecodeNodeLink(reader.GetRemainingBytes(), delivery);
                nodePackets.Enqueue((index, packet));
                if (packet.Kind is NodeLinkMessage.Attach or NodeLinkMessage.Detach)
                {
                    NodeLinkMessage kind = packet.Kind == NodeLinkMessage.Attach ? NodeLinkMessage.AttachAck : NodeLinkMessage.DetachAck;
                    NodeLinkPacket ack = new(kind, packet.ClientId, 0, [], DeliveryMethod.ReliableOrdered);
                    peer.Send(DirectTransportCodec.EncodeNodeLink(ack), (byte)(packet.ClientId % 16), ack.Delivery);
                }
            };
            NetManager manager = TestManager(listener);
            Assert(manager.Start(IPAddress.Loopback, IPAddress.IPv6Any, nodePorts[index]));
            return manager;
        }

        NetManager[] nodes = [StartNode(0), StartNode(1)];

        var options = new GatewayOptions(new(IPAddress.Loopback, gatewayPort), new(IPAddress.Loopback, nodePorts[0]), new(IPAddress.Loopback, controlPort),
            false, null, null, null, null, TimeSpan.FromSeconds(10), null, null, [], DirectFrontend: true);
        string? relayDirectory = null;
        if (gatewayRs != null)
        {
            relayDirectory = Directory.CreateTempSubdirectory("relay-selftest-").FullName;
            options = options with { ExternalRelay = true, RelaySocket = Path.Combine(relayDirectory, "gateway-relay.sock") };
        }
        using var gateway = new DirectGateway(options);
        Exception? gatewayFailure = null;
        var gatewayThread = new Thread(() =>
        {
            try { gateway.Run(); }
            catch (Exception e) { gatewayFailure = e; }
        }) { IsBackground = true, Name = "Gateway-Multiplex-Self-Test" };
        gatewayThread.Start();
        using KillOnDispose? relay = gatewayRs == null ? null : new(StartGatewayRs(gatewayRs, options.RelaySocket!));

        using TestClient first = new(101, gatewayPort);
        using TestClient second = new(202, gatewayPort);
        PumpUntil(() => first.Peer?.ConnectionState == ConnectionState.Connected && second.Peer?.ConnectionState == ConnectionState.Connected
            && nodePackets.Count(x => x.Packet.Kind == NodeLinkMessage.Attach) == 2, [.. nodes, first.Manager, second.Manager]);
        Assert(nodeConnections[0] == 1);
        Assert(nodePackets.Where(x => x.Packet.Kind == NodeLinkMessage.Attach).Select(x => x.Packet.ClientId).Order().SequenceEqual(new ulong[] { 101, 202 }));

        first.Send(11);
        second.Send(22);
        PumpUntil(() => nodePackets.Count(x => x.Packet.Kind == NodeLinkMessage.Relay) == 2, [.. nodes, first.Manager, second.Manager]);
        Assert(nodePackets.Where(x => x.Packet.Kind == NodeLinkMessage.Relay).OrderBy(x => x.Packet.ClientId)
            .Select(x => (x.Packet.ClientId, Value: x.Packet.Payload[^1])).SequenceEqual(new[] { (101UL, (byte)11), (202UL, (byte)22) }));

        SendNodeReply(nodePeers[0]!, 202, 33);
        PumpUntil(() => second.Received.Contains((byte)33), [.. nodes, first.Manager, second.Manager]);
        Assert(!first.Received.Contains((byte)33));
        SendNodeReply(nodePeers[0]!, 202, 66, 6);
        SendNodeReply(nodePeers[0]!, 202, 66, 6);
        PumpUntil(() => second.Received.Count(x => x == 66) == 1, [.. nodes, first.Manager, second.Manager]);
        PumpFor(TimeSpan.FromMilliseconds(50), [.. nodes, first.Manager, second.Manager]);
        Assert(second.Received.Count(x => x == 66) == 1);
        byte[] world = new byte[20_571];
        GameFrame(20, 77).CopyTo(world, 0);
        world[^1] = 77;
        SendNodeReply(nodePeers[0]!, 202, world);
        PumpUntil(() => second.LastPayload?.Length == world.Length, [.. nodes, first.Manager, second.Manager]);
        Assert(second.LastPayload!.SequenceEqual(world));

        first.Peer!.Disconnect();
        PumpUntil(() => nodePackets.Any(x => x.Packet.Kind == NodeLinkMessage.Detach && x.Packet.ClientId == 101), [.. nodes, first.Manager, second.Manager]);
        second.Send(44);
        PumpUntil(() => nodePackets.Any(x => x.Packet.Kind == NodeLinkMessage.Relay && x.Packet.ClientId == 202 && x.Packet.Payload[^1] == 44),
            [.. nodes, first.Manager, second.Manager]);
        // lastNodeActivity must follow the node's packets in both relays: 300 ms of node silence
        // first, so a value frozen at the connect (the external relay before 2026-09-24) fails.
        PumpFor(TimeSpan.FromMilliseconds(300), [.. nodes, first.Manager, second.Manager]);
        DateTimeOffset beforeReply = DateTimeOffset.UtcNow;
        SendNodeReply(nodePeers[0]!, 202, 55);
        PumpUntil(() => second.Received.Contains((byte)55), [.. nodes, first.Manager, second.Manager]);

        using var http = new HttpClient();
        string status = http.GetStringAsync($"http://127.0.0.1:{controlPort}/transport/status").GetAwaiter().GetResult();
        Assert(status.Contains(DirectTransportConstants.WireFingerprint, StringComparison.Ordinal)
            && status.Contains("lastClientActivity", StringComparison.Ordinal));
        using (System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(status))
        {
            System.Text.Json.JsonElement session202 = parsed.RootElement.GetProperty("sessions").EnumerateArray()
                .Single(x => x.GetProperty("client").GetUInt64() == 202);
            DateTimeOffset nodeAt = session202.GetProperty("lastNodeActivity").GetDateTimeOffset();
            if (nodeAt < beforeReply - TimeSpan.FromMilliseconds(50))
                throw new InvalidOperationException($"self-test failed: client 202 lastNodeActivity {nodeAt:O} is older than the node reply sent at {beforeReply:O}");
        }
        using HttpResponseMessage adminHealth = http.GetAsync($"http://127.0.0.1:{controlPort}/admin/v1/health")
            .GetAwaiter().GetResult();
        Assert(adminHealth.IsSuccessStatusCode
            && adminHealth.Headers.GetValues("X-Cluster-Gateway-Protocol").Single() == "1");
        using System.Text.Json.JsonDocument health = System.Text.Json.JsonDocument.Parse(
            adminHealth.Content.ReadAsStream());
        Assert(health.RootElement.GetProperty("protocolVersion").GetInt32() == 1
            && !health.RootElement.GetProperty("data").GetProperty("registryAvailable").GetBoolean());
        using HttpResponseMessage adminStatus = http.GetAsync($"http://127.0.0.1:{controlPort}/admin/v1/status")
            .GetAwaiter().GetResult();
        Assert(adminStatus.StatusCode == HttpStatusCode.ServiceUnavailable);
        using System.Text.Json.JsonDocument unavailable = System.Text.Json.JsonDocument.Parse(
            adminStatus.Content.ReadAsStream());
        Assert(unavailable.RootElement.GetProperty("error").GetProperty("code").GetString() == "registry_unavailable");
        var random = new Random(0x47575903);
        int expectedNode = 0;
        for (int i = 0; i < 32; i++)
        {
            expectedNode = random.Next(nodes.Length);
            Task<HttpResponseMessage> request = http.PostAsync(
                $"http://127.0.0.1:{controlPort}/switch?client=202&backend=127.0.0.1:{nodePorts[expectedNode]}", null);
            PumpUntil(() => request.IsCompleted, [.. nodes, first.Manager, second.Manager]);
            using HttpResponseMessage response = request.GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            AssertRouteModel(nodePackets, 202, expectedNode);

            byte value = (byte)(100 + i);
            second.Send(value);
            PumpUntil(() => nodePackets.Any(x => x.Packet.Kind == NodeLinkMessage.Relay && x.Packet.ClientId == 202
                && x.Packet.Payload[^1] == value), [.. nodes, first.Manager, second.Manager]);
            PumpFor(TimeSpan.FromMilliseconds(50), [.. nodes, first.Manager, second.Manager]);
            var relays = nodePackets.Where(x => x.Packet.Kind == NodeLinkMessage.Relay && x.Packet.ClientId == 202
                && x.Packet.Payload[^1] == value).ToArray();
            Assert(relays.Length == 1 && relays[0].Node == expectedNode);
        }
        Assert(second.Peer?.ConnectionState == ConnectionState.Connected && nodeConnections.All(x => x == 1) && gatewayFailure == null);

        gateway.Stop();
        gatewayThread.Join(2000);
        foreach (NetManager node in nodes) node.Stop();
        if (relayDirectory != null) Directory.Delete(relayDirectory, true);
    }

    /// <summary>Kills a helper process even when the test fails (a leftover would hold the socket and stdout).</summary>
    private sealed class KillOnDispose(Process process) : IDisposable
    {
        public void Dispose()
        {
            try { process.Kill(); process.WaitForExit(2000); } catch (InvalidOperationException) { }
            process.Dispose();
        }
    }

    /// <summary>Starts gateway-rs against the relay socket once ClusterRegistry has bound it.</summary>
    private static Process StartGatewayRs(string binary, string socket)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (!File.Exists(socket) && waited.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
        Assert(File.Exists(socket));
        var start = new ProcessStartInfo(binary) { UseShellExecute = false };
        start.ArgumentList.Add("--socket");
        start.ArgumentList.Add(socket);
        start.ArgumentList.Add("--workers");
        start.ArgumentList.Add("2");
        return Process.Start(start) ?? throw new InvalidOperationException("gateway-rs did not start");
    }

    private static void AssertRouteModel(IEnumerable<(int Node, NodeLinkPacket Packet)> packets, ulong clientId, int expectedNode)
    {
        HashSet<int> attached = [];
        foreach ((int node, NodeLinkPacket packet) in packets.Where(x => x.Packet.ClientId == clientId))
        {
            if (packet.Kind == NodeLinkMessage.Attach)
                Assert(attached.Add(node));
            else if (packet.Kind == NodeLinkMessage.Detach)
                Assert(attached.Remove(node));
            Assert(attached.Count <= 1);
        }
        Assert(attached.SetEquals([expectedNode]));
    }

    private static NetManager TestManager(INetEventListener listener) => new(listener)
    {
        AutoRecycle = true,
        UnsyncedEvents = false,
        ChannelsCount = 16,
    };

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static int FreeTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void PumpUntil(Func<bool> condition, params NetManager[] managers)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            foreach (NetManager manager in managers)
                manager.PollEvents();
            Thread.Sleep(1);
        }
        Assert(condition());
    }

    private static void PumpFor(TimeSpan duration, params NetManager[] managers)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < duration)
        {
            foreach (NetManager manager in managers)
                manager.PollEvents();
            Thread.Sleep(1);
        }
    }

    private static void SendNodeReply(NetPeer peer, ulong clientId, byte value, byte messageId = 7)
        => SendNodeReply(peer, clientId, GameFrame(messageId, value));

    private static void SendNodeReply(NetPeer peer, ulong clientId, byte[] payload)
    {
        NodeLinkPacket packet = new(NodeLinkMessage.Relay, clientId, 2, payload, DeliveryMethod.ReliableOrdered);
        peer.Send(DirectTransportCodec.EncodeNodeLink(packet), (byte)(clientId % 16), packet.Delivery);
    }

    private static byte[] GameFrame(byte messageId, byte value, bool crc = false)
    {
        byte[] frame = [0xCE, crc ? (byte)1 : (byte)0, 0, 0, 0, 0, 0, 1, messageId, 0, value];
        if (crc) BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2), DirectTransportCodec.Crc32(frame.AsSpan(6)));
        return frame;
    }

    private sealed class TestClient : IDisposable
    {
        public readonly NetManager Manager;
        public readonly ConcurrentQueue<byte> Received = new();
        public byte[]? LastPayload;
        public NetPeer? Peer;

        public TestClient(ulong id, int gatewayPort)
        {
            var listener = new EventBasedNetListener();
            listener.PeerConnectedEvent += peer => Peer = peer;
            listener.NetworkReceiveEvent += (_, reader, _, _) =>
            {
                byte[] packet = reader.GetRemainingBytes();
                if (packet.Length >= 12 && packet[0] == 2)
                {
                    LastPayload = packet[1..];
                    Received.Enqueue(packet[^1]);
                }
            };
            Manager = TestManager(listener);
            Assert(Manager.Start());
            Peer = Manager.Connect(IPAddress.Loopback.ToString(), gatewayPort, DirectTransportCodec.EncodeHandshake(id));
        }

        public void Send(byte value)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)2);
            writer.Put(GameFrame(9, value));
            Peer!.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void Dispose() => Manager.Stop();
    }

    /// <summary>SE-796: a keep-world update rewrites Sandbox.sbc; only a changed setting or mod list may change the hash.</summary>
    private static void TestWorldSettingsHash()
    {
        string file = Path.Combine(Path.GetTempPath(), "cluster-world-settings-hash-self-test-" + Guid.NewGuid() + ".sbc");
        string Hash(string text, bool bom = false)
        {
            File.WriteAllText(file, text, new System.Text.UTF8Encoding(bom));
            return ClusterProtocol.WorldSettingsHash.Of(file);
        }
        const string Mod = "<ModItem FriendlyName=\"a\"><Name>1.sbm</Name><PublishedFileId>1</PublishedFileId></ModItem>";
        const string Other = "<ModItem FriendlyName=\"b\"><Name>2.sbm</Name><PublishedFileId>2</PublishedFileId></ModItem>";
        try
        {
            string seed = Hash("<?xml version='1.0' encoding='utf-8'?>\n<MyObjectBuilder_Checkpoint xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">"
                + "<SessionName>Old</SessionName><Settings><A>1</A><B>2</B></Settings><Mods>" + Mod + "</Mods></MyObjectBuilder_Checkpoint>");
            // How activation writes it: another declaration and BOM, indented, settings in another order, another name, world data.
            Assert(seed == Hash("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<MyObjectBuilder_Checkpoint>\r\n  <SessionName>New</SessionName>\r\n"
                + "  <Entities>kept</Entities>\r\n  <Settings>\r\n    <B>2</B>\r\n    <A>1</A>\r\n  </Settings>\r\n  <Mods>\r\n    " + Mod
                + "\r\n  </Mods>\r\n</MyObjectBuilder_Checkpoint>", bom: true));
            string Variant(string settings, string mods) =>
                Hash("<MyObjectBuilder_Checkpoint><Settings>" + settings + "</Settings><Mods>" + mods + "</Mods></MyObjectBuilder_Checkpoint>");
            string same = Variant("<A>1</A><B>2</B>", Mod);
            Assert(same == seed);
            Assert(Variant("<A>1</A><B>3</B>", Mod) != seed);
            Assert(Variant("<A>1</A>", Mod) != seed);
            Assert(Variant("<A>1</A><B>2</B>", Mod + Other) != seed);
            Assert(Variant("<A>1</A><B>2</B>", Other + Mod) != Variant("<A>1</A><B>2</B>", Mod + Other));
        }
        finally { File.Delete(file); }
    }

    private static void TestNodeAdmission()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-admission-self-test-" + Guid.NewGuid());
        Guid instance = Guid.Parse("00000000-0000-0000-0000-000000000001");
        try
        {
            var rotating = new NodeAdmission(CurrentToken, PreviousToken, "cluster", "world", "config", "binary", "types", "settings", 3000);
            NodeJoinRequest valid = Join("node", "endpoint", instance, PreviousToken);
            using (var registry = new GatewayRegistry(directory, rotating))
            {
                AssertConflict(() => registry.RegisterNode(valid with { Token = "wrong" }));
                AssertConflict(() => registry.RegisterNode(valid with { ClusterId = "other" }));
                AssertConflict(() => registry.RegisterNode(valid with { WorldId = "other" }));
                AssertConflict(() => registry.RegisterNode(valid with { ClusterConfigHash = "other" }));
                AssertConflict(() => registry.RegisterNode(valid with { BinaryVersion = "other" }));
                AssertConflict(() => registry.RegisterNode(valid with { ReplicationTypeTableHash = "other" }));
                AssertConflict(() => registry.RegisterNode(valid with { WorldSettingsHash = "other" }));
                AssertConflict(() => registry.RegisterNode(valid with { SyncDistance = 2999 }));
                Assert(registry.Nodes().Length == 0);
                registry.RegisterNode(valid);
                registry.RegisterNode(Join("current", "current-endpoint", Guid.Parse("00000000-0000-0000-0000-000000000002")));
                registry.Checkpoint();
            }
            string persisted = string.Concat(Directory.GetFiles(directory, RegistryStore.FileName + "*")
                .Select(file => System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file))));
            Assert(persisted.Contains("current-endpoint", StringComparison.Ordinal));   // the files ARE the state
            Assert(!persisted.Contains(CurrentToken, StringComparison.Ordinal) && !persisted.Contains(PreviousToken, StringComparison.Ordinal));

            using var currentOnly = new GatewayRegistry(directory, Admission);
            AssertConflict(() => currentOnly.RegisterNode(valid));
            Assert(currentOnly.RegisterNode(valid with { Token = CurrentToken }).Epoch == 1);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// Every command-line option is also settable as CLUSTER_&lt;OPTION&gt;, with the command line on
    /// top. These pass an explicit environment, so an exported variable on the developer's machine
    /// cannot change what they assert.
    private static void TestOptionEnvironment()
    {
        static Func<string, string?> Env(params (string Name, string Value)[] entries) =>
            name => entries.FirstOrDefault(entry => entry.Name == name).Value;

        // Derived from the flag, never tabulated: no option can carry a name that disagrees with it.
        Assert(GatewayOptions.EnvironmentNames.All(entry =>
            entry.Environment == "CLUSTER_" + entry.Flag[2..].Replace('-', '_').ToUpperInvariant()));
        Assert(GatewayOptions.EnvironmentNames.Select(entry => entry.Environment).Distinct(StringComparer.Ordinal).Count()
            == GatewayOptions.EnvironmentNames.Count());

        var accept = ("CLUSTER_ALLOW_TEST_ACCEPT_ALL", "true");
        // The environment stands in for an absent flag, the command line overrides it, and an empty
        // value is the same as unset - so exporting an empty string cannot blank out a default.
        Assert(GatewayOptions.Parse(["--steam"], Env(("CLUSTER_BACKEND", "127.0.0.1:27019"))).Backend.Port == 27019);
        Assert(GatewayOptions.Parse(["--steam", "--backend", "127.0.0.1:27020"],
            Env(("CLUSTER_BACKEND", "127.0.0.1:27019"))).Backend.Port == 27020);
        Assert(GatewayOptions.Parse(["--steam"], Env(("CLUSTER_BACKEND", ""))).Backend.Port == 27016);
        Assert(GatewayOptions.Parse([], Env(("CLUSTER_STEAM", "0.0.0.0:27016"))).SteamListen?.Port == 27016);
        Assert(GatewayOptions.Parse(["--steam"], Env(("CLUSTER_LEASE_TTL_SECONDS", "4"))).LeaseTtl == TimeSpan.FromSeconds(4));
        Assert(GatewayOptions.Parse(["--steam"], Env(("CLUSTER_PAUSED", "yes"))).Paused);
        Assert(!GatewayOptions.Parse(["--steam"], Env(("CLUSTER_PAUSED", "0"))).Paused);
        // A typo in a boolean stops the start rather than quietly clearing the flag.
        AssertArgument(() => GatewayOptions.Parse(["--steam"], Env(("CLUSTER_PAUSED", "treu"))));

        // A value out of the environment is validated exactly as the flag is: same refusals, same
        // fail-closed accept-all gate (Security.md §3.6). Only the source differs.
        AssertArgument(() => GatewayOptions.Parse(["--steam"], Env(("CLUSTER_BACKEND", "8.8.8.8:27016"))));
        AssertArgument(() => GatewayOptions.Parse(["--steam"], Env(("CLUSTER_LEASE_TTL_SECONDS", "0"))));
        AssertArgument(() => GatewayOptions.Parse(["--steam"], Env(("CLUSTER_LEASE_TTL_SECONDS", "soon"))));
        AssertArgument(() => GatewayOptions.Parse([], Env(("CLUSTER_DIRECT", "true"))));
        Assert(GatewayOptions.Parse([], Env(("CLUSTER_DIRECT", "true"), accept)).DirectFrontend);
        AssertArgument(() => GatewayOptions.Parse([], Env(("CLUSTER_DIRECT", "true"),
            ("CLUSTER_LISTEN", "8.8.8.8:27116"), accept)));

        // The generated help lists every option with the variable that stands in for it.
        Assert(GatewayOptions.EnvironmentNames.All(entry =>
            GatewayOptions.Usage.Contains(entry.Environment, StringComparison.Ordinal)));
    }

    private static void TestNetworkSecurityOptions()
    {
        string? networks = Environment.GetEnvironmentVariable("CLUSTER_INTERNAL_NETWORKS");
        string? token = Environment.GetEnvironmentVariable("CLUSTER_ADMIN_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("CLUSTER_INTERNAL_NETWORKS", null);
            Environment.SetEnvironmentVariable("CLUSTER_ADMIN_TOKEN", null);
            AssertArgument(() => GatewayOptions.Parse(["--steam", "--backend", "10.42.0.2:27016"]));
            Environment.SetEnvironmentVariable("CLUSTER_INTERNAL_NETWORKS", "10.42.0.0/24");
            AssertArgument(() => GatewayOptions.Parse(["--steam", "--backend", "10.42.0.2:27016", "--control", "10.42.0.1:8443"]));
            Environment.SetEnvironmentVariable("CLUSTER_ADMIN_TOKEN", CurrentToken);
            Assert(GatewayOptions.Parse(["--steam", "--backend", "10.42.0.2:27016", "--control", "10.42.0.1:8443"])
                is { AdminToken: CurrentToken, InternalNetworks.Length: 1 });
            // --eos logs in but terminates no clients, so it is not a frontend on its own and may
            // not share a port with one that is.
            AssertArgument(() => GatewayOptions.Parse(["--eos", "--backend", "10.42.0.2:27016"]));
            AssertArgument(() => GatewayOptions.Parse(["--steam", "0.0.0.0:27016", "--eos", "0.0.0.0:27016",
                "--backend", "10.42.0.2:27016"]));
            Assert(GatewayOptions.Parse(["--steam", "0.0.0.0:27016", "--eos", "0.0.0.0:27018",
                "--backend", "10.42.0.2:27016"]) is { EosListen.Port: 27018, SteamListen.Port: 27016 });
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLUSTER_INTERNAL_NETWORKS", networks);
            Environment.SetEnvironmentVariable("CLUSTER_ADMIN_TOKEN", token);
        }
    }

    private static void TestSecretRedaction()
    {
        SecretRedactor.Configure([CurrentToken, PreviousToken]);
        string swept = SecretRedactor.Redact("log=" + CurrentToken + " url=?token=" + Uri.EscapeDataString(PreviousToken));
        Assert(!swept.Contains(CurrentToken, StringComparison.Ordinal) && !swept.Contains(PreviousToken, StringComparison.Ordinal)
            && swept.Count(value => value == '[') == 2);
        Assert(typeof(GatewayOptions).GetProperty(nameof(GatewayOptions.AdminToken))!
            .IsDefined(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), inherit: false));
    }

    private static NodeJoinRequest Join(string node, string endpoint, Guid instance, string token = CurrentToken) =>
        new(node, endpoint, instance, token, "cluster", "world", "config", "binary", "types", "settings", 3000);

    /// <summary>
    /// T-0237: a recovery claim names the player bodies whose character home moved to ANOTHER partition after
    /// the covered save was written - the node skips a standing copy of them as stale. Controls: a home that
    /// changed BEFORE the save (a move the save may postdate) and a home naming THIS partition are not listed.
    /// </summary>
    private static void TestRecoveryHomedElsewhere()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            DateTimeOffset now = DateTimeOffset.UnixEpoch.AddHours(1);
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration one = Register(registry, "one", "ep-one", Guid.NewGuid());
            NodeRegistration two = Register(registry, "two", "ep-two", Guid.NewGuid());
            registry.Heartbeat(one.Node, one.Epoch, []);
            registry.Heartbeat(two.Node, two.Epoch, []);
            PartitionOwnership twenty = registry.ClaimPartition(20, one.Node, one.Epoch, 0) with { };
            registry.ClaimPartition(21, one.Node, one.Epoch, 0);
            ReportCharacterHome(registry, 6, 60, 600, 21, one.Node);           // changed BEFORE the save: not listed
            now += TimeSpan.FromSeconds(10);
            registry.RegisterSave(new SaveId(20, twenty.Generation, 1), one.Node, one.Epoch, SaveKind.Final, 1, "g", 2, "one/20-1", "http://one/control");
            now += TimeSpan.FromSeconds(10);
            registry.Heartbeat(one.Node, one.Epoch, [new(20, twenty.Generation), new(21, 1)]);
            ReportCharacterHome(registry, 5, 50, 500, 21, one.Node);           // moved elsewhere AFTER the save: listed
            ReportCharacterHome(registry, 7, 70, 700, 20, one.Node);           // home is this partition: not listed
            registry.Heartbeat(one.Node, one.Epoch, []);                   // leases outlive the clock steps above
            registry.Heartbeat(two.Node, two.Epoch, []);
            registry.ReleasePartition(20, one.Node, one.Epoch, twenty.Generation);
            RecoveryClaim claim = registry.ClaimRecoveries(two.Node, two.Epoch, []).Single(x => x.Ownership.Partition == 20);
            Assert(claim.CharactersHomedElsewhere is { } homed && homed.Contains(500) && !homed.Contains(600) && !homed.Contains(700));
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// T-0237: a recovered copy is ordered against the character's home by home VERSIONS stamped into the save,
    /// not by wall-clock stamps. The clock steps BACKWARD between the save and the home change (an NTP step):
    /// the time rule would keep the stale copy, the version rule drops it. A stamped save without a stamp for a
    /// character never overrides that home, and a home partition that does not hold the body protects the copy.
    /// </summary>
    /// <summary>T-0231: GET /registry/partitions?partition=N answers that one row, or none; the unfiltered report is unchanged.</summary>
    /// <summary>
    /// T-0229: responses stream with per-string redaction. The redacted text equals the old whole-body redaction for
    /// a plain token, a key is redacted too, and a large document streams without large-object copies (the old
    /// path allocated ~6x the body; the Registry peaked at 4-4.6 GB on the 40-125 MB documents the rotator polls).
    /// </summary>
    /// <summary>Sums the runtime's GCAllocationTick events of kind Large (1) raised on the creating thread, in-process
    /// (other threads' allocations - earlier sections' leftovers - are not counted; Linux gettid identifies the thread).</summary>
    private sealed class LargeAllocationListener : System.Diagnostics.Tracing.EventListener
    {
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "gettid")] private static extern int GetTid();
        private long _large;
        private readonly long _osThread = GetTid();
        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
        }
        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (e.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true || e.OSThreadId != _osThread) return;
            int kind = e.PayloadNames!.IndexOf("AllocationKind");
            int amount = e.PayloadNames.IndexOf("AllocationAmount64");
            if (kind < 0 || amount < 0 || Convert.ToInt32(e.Payload![kind]) != 1) return;
            Interlocked.Add(ref _large, Convert.ToInt64(e.Payload[amount]));
        }
        public long TakeLargeBytes()
        {
            Thread.Sleep(200);   // events are dispatched asynchronously
            return Interlocked.Exchange(ref _large, 0);
        }
    }

    /// <summary>
    /// A small response on a kept-alive connection must not wait out Nagle + delayed ACK: chunking every body
    /// (4fb59f6) made each such request ~45 ms, and nodes report character homes synchronously on the game thread
    /// (700-900 ms hitches every 5 s cluster-wide, w32 2026-09-25). Large bodies still stream (chunked) intact.
    /// </summary>
    private static void TestJsonKeepAliveLatency()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)) { probe.Start(); port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var big = Enumerable.Range(0, 20_000).Select(i => new { Partition = (ulong)i, Node = "node-1" }).ToArray();
        var serving = new Thread(() =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext context;
                try { context = listener.GetContext(); } catch { return; }
                if (context.Request.Url!.AbsolutePath == "/big") DirectGateway.WriteJson(context, 200, big);
                else DirectGateway.WriteJson(context, 200, new { stored = true, version = 42L });
            }
        }) { IsBackground = true };
        serving.Start();
        try
        {
            using var client = new HttpClient();
            var times = new List<double>();
            for (int i = 0; i < 8; i++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                string body = client.GetStringAsync($"http://127.0.0.1:{port}/small").GetAwaiter().GetResult();
                times.Add(watch.Elapsed.TotalMilliseconds);
                Assert(body == "{\"stored\":true,\"version\":42}");
            }
            double median = times.Order().ElementAt(times.Count / 2);
            Console.WriteLine($"json-keepalive-latency: small response on a kept-alive connection, median {median:F1} ms of [{string.Join(", ", times.Select(t => t.ToString("F1")))}]");
            Assert(median < 15);
            string bigBody = client.GetStringAsync($"http://127.0.0.1:{port}/big").GetAwaiter().GetResult();
            Assert(bigBody.Length > SpillBytes() && bigBody == System.Text.Json.JsonSerializer.Serialize(big));
        }
        finally { listener.Stop(); }
        static int SpillBytes() => DirectGateway.SpillToChunkedStream.BufferBytes;
    }

    /// <summary>
    /// T-0324: HttpListener sends the headers plus the first 16 KB of a body in one socket write and the rest in a
    /// second; without TCP_NODELAY that second write waits for the client's delayed ACK, so on loopback every
    /// Content-Length response of ~16-65 KB (and the tail of a chunked one) took ~44 ms on a kept-alive connection.
    /// The red arm (a plain listener) must show that stall, or this test proves nothing; the listener with
    /// <see cref="ClusterProtocol.HttpListenerNoDelay"/> must answer every size well below it, the registry's chunked
    /// spill (> 64 KiB) included.
    /// </summary>
    private static void TestHttpNoDelayLatency()
    {
        const double Stall = 10;   // ms: delayed ACK is >= 40 ms on Linux, an unstalled loopback answer ~0.2 ms
        double Median(bool noDelay, int size)
        {
            int port;
            using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)) { probe.Start(); port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
            using var listener = new System.Net.HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            if (noDelay) Assert(ClusterProtocol.HttpListenerNoDelay.Apply(listener, Console.WriteLine) == 1);
            string text = new string('x', size - 2);   // a JSON string: size bytes on the wire
            var serving = new Thread(() =>
            {
                while (listener.IsListening)
                {
                    System.Net.HttpListenerContext context;
                    try { context = listener.GetContext(); } catch { return; }
                    DirectGateway.WriteJson(context, 200, text);
                }
            }) { IsBackground = true };
            serving.Start();
            try
            {
                using var client = new HttpClient();
                var times = new List<double>();
                for (int i = 0; i < 9; i++)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    byte[] body = client.GetByteArrayAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult();
                    times.Add(watch.Elapsed.TotalMilliseconds);
                    Assert(body.Length == size);
                }
                times.RemoveAt(0);   // connection setup
                return times.Order().ElementAt(times.Count / 2);
            }
            finally { listener.Stop(); }
        }

        double red = Median(noDelay: false, 32 * 1024);
        Console.WriteLine($"http-nodelay-latency: 32 KB Content-Length, plain HttpListener (red arm) median {red:F1} ms");
        Assert(red > Stall);
        foreach (int size in new[] { 1024, 32 * 1024, 60 * 1024, 200 * 1024 })
        {
            double median = Median(noDelay: true, size);
            Console.WriteLine($"http-nodelay-latency: {size / 1024} KB {(size > DirectGateway.SpillToChunkedStream.BufferBytes ? "chunked" : "Content-Length")}, TCP_NODELAY median {median:F2} ms");
            Assert(median < Stall);
        }
    }

    private static void TestJsonStreaming()
    {
        SecretRedactor.Configure(["tok0SECRET9"]);
        try
        {
            var sample = new { Name = "a tok0SECRET9 b", Map = new Dictionary<string, int> { ["tok0SECRET9"] = 1 }, Rows = new[] { "x", "tok0SECRET9" } };
            using var streamed = new MemoryStream();
            System.Text.Json.JsonSerializer.Serialize(streamed, sample, DirectGateway.RedactingJson);
            string text = System.Text.Encoding.UTF8.GetString(streamed.ToArray());
            string old = System.Text.Encoding.UTF8.GetString(SecretRedactor.Redact(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(sample)));
            Assert(!text.Contains("tok0SECRET9") && text == old);
            var rows = Enumerable.Range(0, 200_000).Select(i => new { Partition = (ulong)i, Node = "node-1", Generation = i, Retired = i % 3 == 0 }).ToArray();
            long before = GC.GetAllocatedBytesForCurrentThread();
            System.Text.Json.JsonSerializer.Serialize(Stream.Null, rows, DirectGateway.RedactingJson);
            long streamedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            byte[] whole = SecretRedactor.Redact(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(rows));
            long oldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"json-streaming: {whole.Length / 1048576.0:F1} MB document, streamed {streamedBytes / 1048576.0:F1} MB allocated, old path {oldBytes / 1048576.0:F1} MB");
            Assert(streamedBytes * 4 < oldBytes && streamedBytes < whole.Length);

            // A node's body relayed inside a response (/diag sections, trigger fan-outs): redacted per string like the
            // rest, and never a large-object allocation. JsonElement's own converter wrote raw text, so its strings
            // went out unredacted (4fb59f6); the RelayedJson path and the element converter both redact.
            string nodeBody = System.Text.Json.JsonSerializer.Serialize(new
            {
                tok0SECRET9 = "x tok0SECRET9 y", n = 12.5e3, big = 18446744073709551615UL, neg = -3, ok = true, none = (string?)null,
                rows = Enumerable.Range(0, 60_000).Select(i => new { id = i, name = "row " + i, flag = i % 2 == 0 }).ToArray(),
            });
            byte[] nodeBytes = System.Text.Encoding.UTF8.GetBytes(nodeBody);
            Assert(nodeBytes.Length > 3 * RelayedJson.ChunkSize);
            string expected = SecretRedactor.Redact(nodeBody);
            RelayedJson relayed = RelayedJson.Read(new MemoryStream(nodeBytes));
            using var relayedOut = new MemoryStream();
            System.Text.Json.JsonSerializer.Serialize(relayedOut, new { section = (object)relayed }, DirectGateway.RedactingJson);
            string relayedText = System.Text.Encoding.UTF8.GetString(relayedOut.ToArray());
            Assert(!relayedText.Contains("tok0SECRET9") && relayedText == "{\"section\":" + expected + "}");
            using var elementOut = new MemoryStream();
            System.Text.Json.JsonSerializer.Serialize(elementOut, new { section = (object)System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(nodeBytes) }, DirectGateway.RedactingJson);
            Assert(System.Text.Encoding.UTF8.GetString(elementOut.ToArray()) == relayedText);
            using var plainOut = new MemoryStream();
            System.Text.Json.JsonSerializer.Serialize(plainOut, new { section = (object)relayed }, System.Text.Json.JsonSerializerOptions.Default);
            Assert(System.Text.Encoding.UTF8.GetString(plainOut.ToArray()) == "{\"section\":" + nodeBody + "}");
            bool badRejected = false;
            try { RelayedJson.Read(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"a\":1"))); }
            catch (System.Text.Json.JsonException) { badRejected = true; }
            Assert(badRejected);
            // The point is the LARGE-object heap, not the total: the relay makes more small, short-lived strings (one per
            // token, gen0) but no large objects, while the parse path's metadata table and buffers are LOH allocations.
            // Counted in-process from the runtime's AllocationTick events (kind Large), which fire per ~100 KB.
            Assert(RelayedJson.ChunkSize < 85_000);
            // Production shape: the serializer writes into a buffer rented from the shared ArrayPool, so the first call
            // may rent a large array; measured on a second, warm call, as in the Registry's steady state.
            System.Text.Json.JsonSerializer.Serialize(Stream.Null, new { section = (object)RelayedJson.Read(new MemoryStream(nodeBytes)) }, DirectGateway.RedactingJson);
            using var loh = new LargeAllocationListener();
            System.Text.Json.JsonSerializer.Serialize(Stream.Null, new { section = (object)RelayedJson.Read(new MemoryStream(nodeBytes)) }, DirectGateway.RedactingJson);
            long relayLarge = loh.TakeLargeBytes();
            System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(new MemoryStream(nodeBytes));
            long parseLarge = loh.TakeLargeBytes();
            Console.WriteLine($"json-streaming: relayed {nodeBytes.Length / 1048576.0:F1} MB node body: large-object allocation {relayLarge / 1048576.0:F1} MB relayed vs {parseLarge / 1048576.0:F1} MB parsed");
            Assert(parseLarge > 0 && relayLarge == 0);
        }
        finally { SecretRedactor.Configure([]); }
    }

    private static void TestReapEmptyPartition()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            DateTimeOffset now = DateTimeOffset.UnixEpoch.AddHours(1);
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration one = Register(registry, "one", "ep-one", Guid.NewGuid());
            NodeRegistration two = Register(registry, "two", "ep-two", Guid.NewGuid());
            registry.Heartbeat(one.Node, one.Epoch, []);
            registry.Heartbeat(two.Node, two.Epoch, []);
            bool Exists(ulong partition) => registry.PartitionsReport(only: partition).Length == 1;
            void SaveAndRelease(ulong partition, int? entities)
            {
                PartitionOwnership owner = registry.ClaimPartition(partition, one.Node, one.Epoch, null) with { };
                registry.RegisterSave(new SaveId(partition, owner.Generation, 1), one.Node, one.Epoch, SaveKind.Final, 100, "c" + partition, 5,
                    $"one/{partition}-1", "http://one/control", null, entities);
                registry.ReleasePartition(partition, one.Node, one.Epoch, owner.Generation);
            }
            SaveAndRelease(50, 0);      // empty Final: nothing to recover
            SaveAndRelease(51, 1);      // content
            SaveAndRelease(52, null);   // an older build's save: unknown, kept
            registry.ClaimPartition(53, one.Node, one.Epoch, 0);
            ReportCharacterHome(registry, 9, 90, 900, 53, one.Node);
            SaveAndRelease(53, 0);      // empty, but a character calls it home
            registry.ClaimPartition(54, one.Node, one.Epoch, 0);
            ReportCharacterHome(registry, 8, 80, 800, 54, one.Node);
            SaveAndRelease(54, 0);      // the same, for the auto-pick path
            Check(!Exists(50), "an empty released partition is reaped at release");
            // SE-835: a stale world seed would start it again at generation 1, below its saves.
            AssertConflict(() => registry.ClaimPartition(50, two.Node, two.Epoch, 0));
            Check(!Exists(50), "a reaped partition does not come back through a startup claim");
            Check(Exists(51), "a partition whose newest save holds content is kept (the control)");
            Check(Exists(52), "a save without an entity count is never read as empty");
            Check(Exists(53) && Exists(54), "a partition a character calls home is kept");
            PartitionOwnership newHome = registry.ClaimPartition(51, one.Node, one.Epoch, null);
            ReportCharacterHome(registry, 9, 90, 900, 51, one.Node);
            ReportCharacterHome(registry, 8, 80, 800, 51, one.Node);
            registry.ReleasePartition(51, one.Node, one.Epoch, newHome.Generation);
            RecoveryClaim[] claims = registry.ClaimRecoveries(two.Node, two.Epoch, [53]);
            Check(claims.Length == 0 && !Exists(53), "once the home moves, a requested claim reaps the empty row instead of handing it out");
            Check(Exists(54), "a requested claim checks only the rows it names");
            claims = registry.ClaimRecoveries(two.Node, two.Epoch, []);
            Check(!Exists(54), "the auto-pick reaps an empty candidate");
            Check(claims.Length == 1 && claims[0].Ownership.Partition is 51 or 52, "and still recovers a content row");
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("reap-empty-partition: " + what); }
    }

    /// <summary>
    /// T-0388: the registry side of the node's empty-recovery release. 321894 (w33 2026-10-03): node-277's save held one
    /// entity, node-282 recovered generation 3 with that entity screened out as live on another node, catalogued an empty
    /// Final and held the row for two hours; its drain's memberless release then had it reaped. The node now releases at
    /// once, after the empty Final: the row is reaped and recovery never offers it again. The falsifier is the order the
    /// node must not use - released before its empty Final, the row goes back to recovery with the OLDER save that still
    /// holds the entity as its newest, the copy that resurfaces beside the live one whenever the census misses it (the
    /// w33 2026-09-27 73626 shape).
    /// </summary>
    private static void TestRecoveredEmptyRelease()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            DateTimeOffset now = DateTimeOffset.UnixEpoch.AddHours(1);
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration one = Register(registry, "one", "ep-one", Guid.NewGuid());
            NodeRegistration two = Register(registry, "two", "ep-two", Guid.NewGuid());
            registry.Heartbeat(one.Node, one.Epoch, []);
            registry.Heartbeat(two.Node, two.Epoch, []);
            bool Exists(ulong partition) => registry.PartitionsReport(only: partition).Length == 1;
            long clusterTime = 10;
            // one owns the partition and catalogues its single entity, then loses it (a release here; a lease expiry live).
            // two recovers it: the claim's newest covering save is that content save.
            PartitionOwnership Recovered(ulong partition)
            {
                PartitionOwnership first = registry.ClaimPartition(partition, one.Node, one.Epoch, null) with { };
                registry.RegisterSave(new SaveId(partition, first.Generation, 1), one.Node, one.Epoch, SaveKind.Periodic, 100, "c" + partition,
                    clusterTime++, $"one/{partition}-1", "http://one/control", null, 1);
                registry.ReleasePartition(partition, one.Node, one.Epoch, first.Generation);
                RecoveryClaim[] claims = registry.ClaimRecoveries(two.Node, two.Epoch, [partition]);
                Check(claims.Length == 1 && claims[0].ResolveCover.Any(item => item.Save.SaveId == new SaveId(partition, first.Generation, 1)),
                    $"partition {partition}: the content row is recovered from its content save");
                PartitionOwnership claimed = claims[0].Ownership;
                registry.Heartbeat(two.Node, two.Epoch, [new PartitionVersion(partition, claimed.Generation)]);
                return claimed;
            }

            PartitionOwnership fixedOrder = Recovered(70);
            registry.RegisterSave(new SaveId(70, fixedOrder.Generation, 1), two.Node, two.Epoch, SaveKind.Final, 50, "e70",
                clusterTime++, "two/70-1", "http://two/control", null, 0);
            registry.ReleasePartition(70, two.Node, two.Epoch, fixedOrder.Generation);
            Check(!Exists(70), "released after its empty Final, the recovered-empty row is reaped at once (no row left for the drain)");
            bool offered;
            try { offered = registry.ClaimRecoveries(one.Node, one.Epoch, [70]).Length != 0; }
            catch (RegistryConflictException) { offered = false; }
            Check(!offered, "and recovery never offers it again (no re-claim loop)");
            Check(registry.ClaimRecoveries(one.Node, one.Epoch, []).All(claim => claim.Ownership.Partition != 70), "not even to the auto-pick");

            // The falsifier: the same recovery released BEFORE its empty Final.
            PartitionOwnership early = Recovered(71);
            registry.ReleasePartition(71, two.Node, two.Epoch, early.Generation);
            Check(Exists(71), "falsifier: released before its empty Final, the row is kept");
            RecoveryClaim[] again = registry.ClaimRecoveries(one.Node, one.Epoch, [71]);
            Check(again.Length == 1 && again[0].ResolveCover.Any(item => item.Save.SaveId == new SaveId(71, early.Generation - 1, 1) && item.Save.Entities == 1),
                "falsifier: and recovery offers the older save that still holds the entity - why the node waits for its Final");
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("recovered-empty-release: " + what); }
    }

    private static void TestPartitionsReportFilter()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            DateTimeOffset now = DateTimeOffset.UnixEpoch.AddHours(1);
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration one = Register(registry, "one", "ep-one", Guid.NewGuid());
            registry.Heartbeat(one.Node, one.Epoch, []);
            registry.ClaimPartition(40, one.Node, one.Epoch, 0);
            registry.ClaimPartition(41, one.Node, one.Epoch, 0);
            int all = registry.PartitionsReport().Length;
            object[] single = registry.PartitionsReport(only: 41);
            Assert(all >= 2 && single.Length == 1 && System.Text.Json.JsonSerializer.Serialize(single[0]).Contains("\"Partition\":41"));
            Assert(registry.PartitionsReport(only: 999).Length == 0);
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// T-0237: an automatic handover must not move the partition a still-joining client is placed in or has its home
    /// body in (w33 2026-09-27 12:12:22: 91594 moved with pilot-79 while Pilot-103, placed there and seated, was 7 s
    /// from world-ready; 103 got a new body and the seated one was despawned as a duplicate).
    /// </summary>
    private static void TestJoiningClientPlacement()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch.AddHours(1);
        var placement = new Dictionary<ulong, ulong> { [103] = 91594, [79] = 91594, [7] = 500, [8] = 600 };
        var home = new Dictionary<ulong, ulong> { [103] = 91594, [79] = 91594, [7] = 91594, [8] = 600 };
        ulong Check(params (ulong, bool, DateTimeOffset)[] sessions) => DirectGateway.JoiningClientPlacedIn(sessions, 91594,
            c => placement.TryGetValue(c, out ulong p) ? p : null, c => home.TryGetValue(c, out ulong h) ? h : null, now);
        static void Assert(bool value, string what) { if (!value) throw new InvalidOperationException("joining-client-placement: " + what); }
        DateTimeOffset longAgo = now - TimeSpan.FromMinutes(5), justNow = now - TimeSpan.FromSeconds(3);
        Assert(Check((79, true, longAgo), (103, false, default)) == 103, "a client still loading the world defers its partition (Pilot-103)");
        Assert(Check((79, true, longAgo), (103, true, justNow)) == 103, "a client ready under the settle still defers it");
        Assert(Check((79, true, longAgo), (103, true, longAgo)) == 0, "settled clients do not defer it");
        Assert(Check((7, false, default)) == 7, "a joining client whose HOME body is there defers it, wherever it is placed");
        Assert(Check((8, false, default)) == 0, "a joining client placed and homed elsewhere does not");
        Assert(Check() == 0, "no sessions, no deferral");
        // T-0296 r3: the client-bearing drains (CloseNode, DrainClosedNodes) and the operator /handover move a partition
        // with the crew its node REPORTS. A client still joining into the same partition is not in that report, so the
        // same Pilot-103 interleaving ran there unguarded: the partition leaves with pilot-79, 103's body with it.
        ulong Blocker(bool settleCrew, ulong[] crew, params (ulong, bool, DateTimeOffset)[] sessions) =>
            DirectGateway.ClientBearingMoveBlocker(sessions, crew, 91594,
                c => placement.TryGetValue(c, out ulong p) ? p : null, c => home.TryGetValue(c, out ulong h) ? h : null, now, settleCrew);
        Assert(Blocker(true, [79], (79, true, longAgo), (103, false, default)) == 103, "a drain with crew 79 defers while 103 is still loading into the partition");
        Assert(Blocker(false, [79], (79, true, longAgo), (103, true, justNow)) == 103, "an operator handover with crew 79 is refused while 103 is inside its settle");
        Assert(Blocker(true, [79], (79, true, longAgo), (7, false, default)) == 7, "a joining client whose HOME body is there defers the drain");
        Assert(Blocker(true, [79, 103], (79, true, longAgo), (103, true, longAgo)) == 0, "a settled crew moves");
        Assert(Blocker(true, [79], (79, true, longAgo), (8, false, default)) == 0, "a client joining elsewhere does not defer it");
        Assert(Blocker(true, [79, 103], (79, true, longAgo), (103, true, justNow)) == 103, "a drain waits out a crew member's post-ready settle (T-0238)");
        Assert(Blocker(false, [79, 103], (79, true, longAgo), (103, true, justNow)) == 0, "an operator handover of a just-ready crew is honoured as asked");
    }

    private static void TestRecoveryHomeVersions()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            DateTimeOffset now = DateTimeOffset.UnixEpoch.AddHours(1);
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration one = Register(registry, "one", "ep-one", Guid.NewGuid());
            NodeRegistration two = Register(registry, "two", "ep-two", Guid.NewGuid());
            registry.Heartbeat(one.Node, one.Epoch, []);
            registry.Heartbeat(two.Node, two.Epoch, []);
            PartitionOwnership thirty = registry.ClaimPartition(30, one.Node, one.Epoch, 0) with { };
            registry.ClaimPartition(31, one.Node, one.Epoch, 0);                         // live home
            PartitionOwnership empty = registry.ClaimPartition(32, one.Node, one.Epoch, 0) with { };   // released, body NOT in its save
            PartitionOwnership holds = registry.ClaimPartition(33, one.Node, one.Epoch, 0) with { };   // released, body in its save
            long v300 = ReportCharacterHome(registry, 3, 30, 300, 30, one.Node);
            long v500 = ReportCharacterHome(registry, 5, 50, 500, 30, one.Node);
            long v600 = ReportCharacterHome(registry, 6, 60, 600, 30, one.Node);
            Check(v300 > 0 && v500 > v300 && v600 > v500, "versions are handed out in order");
            Check(ReportCharacterHome(registry, 3, 30, 300, 30, one.Node) == v300, "a repeated report keeps its version");
            now += TimeSpan.FromSeconds(10);
            registry.RegisterSave(new SaveId(30, thirty.Generation, 1), one.Node, one.Epoch, SaveKind.Final, 1, "g", 2, "one/30-1", "http://one/control",
                new Dictionary<long, long> { [300] = v300, [500] = v500, [600] = v600 });
            now -= TimeSpan.FromSeconds(30);                                             // the clock steps back
            ReportCharacterHome(registry, 3, 30, 300, 31, one.Node);                         // moved on: stale copy, earlier wall time
            ReportCharacterHome(registry, 4, 40, 400, 31, one.Node);                         // no stamp in a stamped save: kept
            ReportCharacterHome(registry, 5, 50, 500, 32, one.Node);                         // home partition lacks the body: kept
            ReportCharacterHome(registry, 6, 60, 600, 33, one.Node);                         // home partition's save holds it: dropped
            registry.RegisterSave(new SaveId(32, empty.Generation, 1), one.Node, one.Epoch, SaveKind.Final, 1, "g", 2, "one/32-1", "http://one/control",
                new Dictionary<long, long>());
            registry.RegisterSave(new SaveId(33, holds.Generation, 1), one.Node, one.Epoch, SaveKind.Final, 1, "g", 5, "one/33-1", "http://one/control",
                new Dictionary<long, long> { [600] = v600 + 3 });
            registry.Heartbeat(one.Node, one.Epoch, []);
            registry.Heartbeat(two.Node, two.Epoch, []);
            registry.ReleasePartition(32, one.Node, one.Epoch, empty.Generation);
            registry.ReleasePartition(33, one.Node, one.Epoch, holds.Generation);
            registry.ReleasePartition(30, one.Node, one.Epoch, thirty.Generation);
            RecoveryClaim claim = registry.ClaimRecoveries(two.Node, two.Epoch, [30]).Single(x => x.Ownership.Partition == 30);
            long[] homed = claim.CharactersHomedElsewhere ?? [];
            static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("recovery-home-versions: " + what); }
            Check(homed.Contains(300), "a copy whose stamp is below the home's version is stale even when the clock stepped back");
            Check(!homed.Contains(400), "a stamped save without a stamp for the character never overrides its home");
            Check(!homed.Contains(500), "a home partition that does not hold the body protects the only copy");
            Check(homed.Contains(600), "a home partition whose save holds the body lets the stale copy go");
            // By identity (Pilot-010, w32 2026-09-25 04:16): the node gets every home elsewhere that holds its body,
            // keyed by identity, plus the cover's stamps, so it can drop an older DIFFERENT body of the same player.
            Check(claim.HomesElsewhere is { } homes && homes.Any(h => h.Identity == 30 && h.Entity == 300 && h.Partition == 31 && h.Version > v300)
                && homes.Any(h => h.Identity == 60) && !homes.Any(h => h.Identity == 50), "homes elsewhere carry identity, entity and version, never a home without the body");
            Check(claim.SaveStamped && claim.SaveHomeStamps is { } saved && saved[300] == v300 && claim.SaveCreated is not null, "the claim carries the cover's stamps");
            // Capture times (Pilot-028, w33 2026-09-27): the node orders an UNSTAMPED copy of the home's own body against
            // the home partition's newest save that stamps it, on the cluster clock.
            Check(claim.SaveCaptured == 2, "the claim carries the cover's newest capture time");
            Check(claim.HomesElsewhere!.Single(h => h.Identity == 60).Captured == 5, "a home carries its partition's stamped copy's capture time");
            Check(claim.HomesElsewhere!.Single(h => h.Identity == 30).Captured == 0, "a home partition with no stamping save carries none");
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// T-0237: a Closed node's drain may take a partition OFFLINE in place of the release (a cluster stop, a
    /// rotation) - only with the Drain flag. The same offer without it is refused (the offline sweep's rule:
    /// a Closed node offers nothing), which is the control that the flag, not a relaxed node check, admits
    /// it. The row then behaves as any offline row: unowned, not pending recovery, invisible to auto-pick.
    /// </summary>
    private static void TestDrainOffline()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration one = Register(registry, "one", "ep-one", Guid.NewGuid());
            NodeRegistration two = Register(registry, "two", "ep-two", Guid.NewGuid());
            registry.Heartbeat(one.Node, one.Epoch, []);
            registry.Heartbeat(two.Node, two.Epoch, []);
            PartitionOwnership eleven = registry.ClaimPartition(11, one.Node, one.Epoch, 0) with { };
            registry.RegisterSave(new SaveId(11, eleven.Generation, 1), one.Node, one.Epoch, SaveKind.Final, 1, "e", 2, "one/11-1", "http://one/control");
            registry.CloseNode(one.Node, one.Epoch, TimeSpan.FromMinutes(1));
            OfflineRequest offer = new(11, one.Node, one.Epoch, eleven.Generation, 1, 100, 0, 0, 50, [[100, 0, 0, 50]], [110], 1000, "[]", "[]");
            try { registry.TakeOffline(offer); Assert(false); } catch (RegistryConflictException) { }
            Assert(!registry.IsOffline(11) && registry.GetPartitionOwner(11).Node == one.Node);
            OfflinePartition row = registry.TakeOffline(offer with { Drain = true });
            Assert(row.Save == new SaveId(11, eleven.Generation, 1) && registry.IsOffline(11));
            Assert(ReferenceEquals(registry.TakeOffline(offer with { Drain = true }), row));   // idempotent after a lost reply
            Assert(registry.GetPartitionOwner(11) is { Node: null, Recovering: false });
            Assert(!registry.RecoveryPending());
            Assert(registry.ClaimRecoveries(two.Node, two.Epoch, []).Length == 0);
            // A stranger's drain offer is still fenced by ownership.
            PartitionOwnership twelve = registry.ClaimPartition(12, two.Node, two.Epoch, 0) with { };
            registry.RegisterSave(new SaveId(12, twelve.Generation, 1), two.Node, two.Epoch, SaveKind.Final, 1, "f", 2, "two/12-1", "http://two/control");
            try { registry.TakeOffline(new OfflineRequest(12, one.Node, one.Epoch, twelve.Generation, 1, 0, 0, 0, 1, [[0, 0, 0, 1]], [120], 1, "[]", "[]") { Drain = true }); Assert(false); }
            catch (RegistryConflictException) { }
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// T-0193 offline partitions, the registry half: the transition is owner-fenced and needs the Final
    /// catalogued as the partition's newest save; an offline row is invisible to every generic claim
    /// path (auto-pick, explicit warm list, startup claim) and to RecoveryPending; a directed load goes
    /// to its target only; the row ends when the new owner reports members, not when the claim lands;
    /// and the table survives a registry restart. Each negative has its positive control in the same
    /// fixture, so a green here cannot come from a claim path that simply never returns anything.
    /// </summary>
    private static void TestOfflinePartitions()
    {
        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using (var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)))
            {
                NodeRegistration one = Register(registry, "one", "ep-one", Guid.NewGuid());
                NodeRegistration two = Register(registry, "two", "ep-two", Guid.NewGuid());
                registry.Heartbeat(one.Node, one.Epoch, []);
                registry.Heartbeat(two.Node, two.Epoch, []);
                PartitionOwnership seven = registry.ClaimPartition(7, one.Node, one.Epoch, 0);
                PartitionOwnership eight = registry.ClaimPartition(8, one.Node, one.Epoch, 0);
                PartitionOwnership nine = registry.ClaimPartition(9, one.Node, one.Epoch, 0);
                // ClaimPartition hands back the LIVE row, which the transitions below mutate: pin the values.
                (seven, eight, nine) = (seven with { }, eight with { }, nine with { });
                registry.RegisterSave(new SaveId(7, seven.Generation, 1), one.Node, one.Epoch, SaveKind.Periodic, 1, "a", 1, "one/7-1", "http://one/control");
                registry.RegisterSave(new SaveId(7, seven.Generation, 2), one.Node, one.Epoch, SaveKind.Final, 1, "b", 2, "one/7-2", "http://one/control");
                registry.RegisterSave(new SaveId(8, eight.Generation, 1), one.Node, one.Epoch, SaveKind.Final, 1, "c", 2, "one/8-1", "http://one/control");
                registry.RegisterSave(new SaveId(9, nine.Generation, 1), one.Node, one.Epoch, SaveKind.Final, 1, "d", 2, "one/9-1", "http://one/control");
                OfflineRequest Offer(ulong partition, long generation, long sequence, string node = "one", long? epoch = null) =>
                    new(partition, node, epoch ?? one.Epoch, generation, sequence, 100, 0, 0, 50, [[100, 0, 0, 50]], [(long)partition * 10], 1000, "[]", "[]");

                // Refusals: not the newest save, wrong owner. Neither changes anything.
                try { registry.TakeOffline(Offer(7, seven.Generation, 1)); Assert(false); } catch (RegistryConflictException) { }
                try { registry.TakeOffline(Offer(7, seven.Generation, 2, "two", two.Epoch)); Assert(false); } catch (RegistryConflictException) { }
                Assert(!registry.IsOffline(7) && registry.GetPartitionOwner(7).Node == one.Node);

                OfflinePartition row = registry.TakeOffline(Offer(7, seven.Generation, 2));
                Assert(row.Save == new SaveId(7, seven.Generation, 2) && registry.IsOffline(7));
                Assert(ReferenceEquals(registry.TakeOffline(Offer(7, seven.Generation, 2)), row));   // idempotent after a lost reply
                Assert(registry.GetPartitionOwner(7) is { Node: null, Recovering: false });
                // Durable before the answer: the directory copied right after the commit flush (no Dispose, as
                // after a crash) reopens with the row; copied before it - the falsifier - it does not.
                string Copy()
                {
                    string target = Directory.CreateTempSubdirectory("gw-selftest-crash-").FullName;
                    foreach (string file in Directory.GetFiles(directory))
                        File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
                    return target;
                }
                string beforeFlush = Copy();
                registry.FlushOfflineCommit(TimeSpan.Zero);
                string afterFlush = Copy();
                using (var crashed = new GatewayRegistry(beforeFlush, Admission, () => now, TimeSpan.FromSeconds(10)))
                    Assert(!crashed.IsOffline(7));
                using (var crashed = new GatewayRegistry(afterFlush, Admission, () => now, TimeSpan.FromSeconds(10)))
                    Assert(crashed.IsOffline(7) && crashed.GetPartitionOwner(7) is { Node: null });
                Directory.Delete(beforeFlush, true);
                Directory.Delete(afterFlush, true);
                registry.TakeOffline(Offer(8, eight.Generation, 1));

                // Unowned by design, so not pending recovery; partition 9's release is the control that
                // the same question still answers true for an ordinary unowned row.
                Assert(!registry.RecoveryPending());
                registry.ReleasePartition(9, one.Node, one.Epoch, nine.Generation);
                Assert(registry.RecoveryPending());
                RecoveryClaim[] generic = registry.ClaimRecoveries(two.Node, two.Epoch, []);
                Assert(generic.Length == 1 && generic[0].Ownership.Partition == 9);   // the auto-pick works, and skips 7 and 8
                Assert(registry.ClaimRecoveries(two.Node, two.Epoch, []).Length == 0);
                Assert(registry.ClaimRecoveries(two.Node, two.Epoch, [7]).Length == 0);   // explicit warm list filters it
                try { registry.ClaimPartition(7, two.Node, two.Epoch, null); Assert(false); } catch (RegistryConflictException) { }

                // Directed: only the target claims, and ahead of anything else it asked for.
                Assert(registry.RequestOfflineLoad(7, two.Node, "approach") == "requested:two");
                Assert(registry.ClaimRecoveries(one.Node, one.Epoch, []).Length == 0);
                RecoveryClaim[] directed = registry.ClaimRecoveries(two.Node, two.Epoch, []);
                Assert(directed.Length == 1 && directed[0].Ownership.Partition == 7
                    && directed[0].ResolveCover.Any(cover => cover.Save.SaveId == new SaveId(7, seven.Generation, 2)));
                long loadedGeneration = directed[0].Ownership.Generation;
                Assert(registry.RequestOfflineLoad(7, one.Node, "approach") == "loading:two");
                // A node being drained never takes a directed load, named or least-loaded (w32 21:21:38, 253350).
                registry.DrainingNodes = () => [one.Node, two.Node];
                Assert(registry.RequestOfflineLoad(8, one.Node, "join") == "no-target");
                registry.DrainingNodes = null;
                // A pending claim is reported at zero members: still offline. Members end it.
                registry.Heartbeat(two.Node, two.Epoch, [new PartitionVersion(7, loadedGeneration, 0)]);
                Assert(registry.IsOffline(7));
                registry.Heartbeat(two.Node, two.Epoch, [new PartitionVersion(7, loadedGeneration, 3)]);
                Assert(!registry.IsOffline(7) && registry.GetPartitionOwner(7).Node == two.Node);
                Assert(registry.IsOffline(8));
            }
            // The table is durable: a restarted registry still knows 8 is offline and still refuses it
            // to the auto-pick.
            using (var reopened = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10)))
            {
                Assert(reopened.IsOffline(8) && !reopened.IsOffline(7));
                Assert(reopened.OfflinePartitions(0).Partitions.Single().Partition == 8);
            }
        }
        finally
        {
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }
    }

    private static NodeRegistration Register(GatewayRegistry registry, string node, string endpoint, Guid instance) =>
        registry.RegisterNode(Join(node, endpoint, instance));

    /// <summary>
    /// Orchestration §3.3 / Partitioning §6.10 containment: consecutive same-signature validation aborts
    /// block a partition's scheduling with exponential backoff; a new signature restarts the count; and
    /// the three - only three - clear conditions each work: committed handover, membership change
    /// (covered by the lineage path), admin trigger.
    /// </summary>
    /// <summary>
    /// Orchestration §5.1 run-root provenance: same-identity reuse is a legal warm start, a different
    /// world against the same durable root is refused at construction with both identities named, and
    /// a wipe resolves it.
    /// </summary>
    private static void TestProvenance()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-self-test-" + Guid.NewGuid());
        try
        {
            using (new GatewayRegistry(directory, Admission)) { }
            Assert(File.Exists(Path.Combine(directory, "provenance.json")));
            using (new GatewayRegistry(directory, Admission)) { }   // same identity: warm reuse stays legal
            var otherWorld = new NodeAdmission(CurrentToken, null, "cluster", "other-world", "config", "binary", "types", "settings", 3000);
            bool refused = false;
            try { using var _ = new GatewayRegistry(directory, otherWorld); }
            catch (InvalidOperationException e)
            {
                refused = e.Message.Contains("'world'") && e.Message.Contains("'other-world'") && e.Message.Contains("wipe");
            }
            Assert(refused);
            Directory.Delete(directory, recursive: true);           // the wipe resolution
            using (new GatewayRegistry(directory, otherWorld)) { }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void TestHandoverBlocked()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-self-test-" + Guid.NewGuid());
        try
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch.AddDays(20000);
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromHours(1));
            NodeRegistration source = Register(registry, "n1", "127.0.0.1:27016", Guid.NewGuid());
            NodeRegistration target = Register(registry, "n2", "127.0.0.1:27018", Guid.NewGuid());
            PartitionOwnership owner = registry.ClaimPartition(7, source.Node, source.Epoch, 0);

            registry.RecordValidationAbort(7, "aaaa", "gate");
            Assert(!registry.HandoverScheduleBlocked(7));            // one abort is a retry, not a block
            registry.RecordValidationAbort(7, "aaaa", "gate");
            Assert(registry.HandoverScheduleBlocked(7));             // the second same-signature abort blocks
            now += TimeSpan.FromSeconds(61);
            Assert(!registry.HandoverScheduleBlocked(7));            // 1 min backoff expired: one retry allowed
            registry.RecordValidationAbort(7, "aaaa", "gate");
            now += TimeSpan.FromSeconds(61);
            Assert(registry.HandoverScheduleBlocked(7));             // backoff doubled to 2 min
            now += TimeSpan.FromSeconds(60);
            Assert(!registry.HandoverScheduleBlocked(7));

            registry.RecordValidationAbort(7, "bbbb", "gate");       // signature drift: same episode, count continues
            Assert(registry.HandoverScheduleBlocked(7));             // attempts=4, backoff doubled again to 4 min
            now += TimeSpan.FromSeconds(241);
            Assert(!registry.HandoverScheduleBlocked(7));

            registry.RecordValidationAbort(7, "aaaa", "gate");
            Assert(registry.HandoverScheduleBlocked(7));
            Assert(registry.ClearHandoverBlock(7));                  // admin trigger starts the slate clean
            Assert(!registry.HandoverScheduleBlocked(7));
            Assert(!registry.ClearHandoverBlock(7));

            registry.RecordValidationAbort(7, "aaaa", "gate");
            registry.RecordValidationAbort(7, "aaaa", "gate");
            Assert(registry.HandoverScheduleBlocked(7));
            registry.ReleaseHandoverBackoff(7, "membership-change"); // a lineage op buys one prompt retry
            Assert(!registry.HandoverScheduleBlocked(7));
            registry.RecordValidationAbort(7, "aaaa", "gate");       // the retry aborted again
            Assert(registry.HandoverScheduleBlocked(7));             // re-blocked immediately, backoff escalated
            Assert(registry.ClearHandoverBlock(7));

            registry.RecordValidationAbort(7, "aaaa", "gate");
            registry.RecordValidationAbort(7, "aaaa", "gate");
            Assert(registry.HandoverScheduleBlocked(7));
            registry.Begin("h1", 7, source.Node, source.Epoch, owner.Generation, target.Node, target.Epoch);
            registry.Activate("h1");
            registry.Commit("h1");                                   // a committed handover clears the block
            Assert(!registry.HandoverScheduleBlocked(7));
            Assert(registry.HandoverBlocks().Length == 0);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>
    /// T-0308: the node retries a topology commit with the SAME op id when the reply is lost (10 s
    /// timeout). If the registry committed the first attempt and a graceful stop then closed the node,
    /// the retry used to draw "is Closed" before the op-id lookup, and the node treats that 409 as
    /// "never committed": it reverts its layouts to the retired inputs while the registry has already
    /// handed their members to the outputs. An exact replay by the same incarnation must get the
    /// committed record back; a differing replay and an old incarnation stay refused, and a NEW
    /// change from a Closed node is still refused.
    /// </summary>
    private static void TestNodeScaling()
    {
        // T-0312: the desired regular node count and the scale-in retiring/retired sets live in the registry,
        // survive a restart, keep retiring nodes from taking new content, and let a raised target take back
        // a retiring node that has not closed yet.
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("node-scaling: " + what); }
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-self-test-" + Guid.NewGuid());
        try
        {
            NodeRegistration n1, n2, n3;
            using (var registry = new GatewayRegistry(directory, Admission))
            {
                n1 = Register(registry, "node-1", "127.0.0.1:27101", Guid.NewGuid());
                n2 = Register(registry, "node-2", "127.0.0.1:27102", Guid.NewGuid());
                n3 = Register(registry, "node-3", "127.0.0.1:27103", Guid.NewGuid());
                foreach (NodeRegistration n in new[] { n1, n2, n3 }) registry.Heartbeat(n.Node, n.Epoch, []);
                PartitionOwnership owned = registry.ClaimPartition(40, n3.Node, n3.Epoch, 0);
                Check(registry.Scale(true).Phase == "unset", "no target yet: phase unset");
                long revision = registry.SetScaleTarget(2, force: false);
                ScaleStatus status = registry.Scale(true);
                Check(revision == 1 && status.Target == 2 && status.Phase == "scaling-in" && status.Serving.Length == 3,
                    $"target 2 with 3 serving is scaling-in: {status.Phase} {status.Serving.Length}");
                registry.BeginRetiring(["node-3", "node-9"], "test");
                Check(registry.RetiringNodes().SequenceEqual(["node-3"]), "only a serving regular can retire");
                Check(registry.Scale(true).Serving.SequenceEqual(["node-1", "node-2"]), "a retiring node no longer counts as serving");
                // No new content onto a retiring node: recovery claims come back empty (not an error), and
                // offline loads pick another node.
                Check(registry.ClaimRecoveries(n3.Node, n3.Epoch, []).Length == 0, "a retiring node claims no recoveries");
                Check(registry.SettleRetiring(new HashSet<string> { "node-3" }).Length == 0, "a retiring node that still owns a partition is not closed");
                // Supersede: raising the target back takes the not-yet-closed retiring node back.
                registry.SetScaleTarget(3, force: false);
                Check(registry.RetiringNodes().Length == 0 && registry.Scale(true).Serving.Length == 3, "a raised target cancels retiring");
                registry.SetScaleTarget(2, force: true);
                registry.BeginRetiring(["node-3"], "test again");
                // r34 2026-09-30 7->5: node-4 drained every member but still held a MEMBERLESS partition row (its
                // grid had left); the report listed nothing, the row blocked the close, the scale-in waited forever.
                // Closing is what releases such a row (the node's drain), so the report decides, not the row.
                Check(registry.SettleRetiring(new HashSet<string>()).SequenceEqual(["node-3"]),
                    "a retiring node whose report owns nothing is closed although a memberless row still names it");
                registry.ReleasePartition(40, n3.Node, n3.Epoch, owned.Generation);
                string[] toClose = registry.SettleRetiring(new HashSet<string>());
                Check(toClose.SequenceEqual(["node-3"]), "a retiring node that owns nothing is returned for closing: " + string.Join(",", toClose));
                registry.CloseNode(n3.Node, n3.Epoch, TimeSpan.FromSeconds(60));
                registry.MarkEmpty(n3.Node, n3.Epoch);
                registry.SettleRetiring(new HashSet<string>());
                status = registry.Scale(true);
                Check(status.Retired.SequenceEqual(["node-3"]) && status.Retiring.Length == 0, "an Empty retiring node is retired");
                Check(status.Force && status.Revision == 3, "force and revision recorded");
            }
            using (var reopened = new GatewayRegistry(directory, Admission))
            {
                ScaleStatus status = reopened.Scale(true);
                Check(status.Target == 2 && status.Revision == 3 && status.Retired.SequenceEqual(["node-3"]),
                    $"scale state survives a registry restart: {status.Target} r{status.Revision} [{string.Join(",", status.Retired)}]");
                Register(reopened, "node-3", "127.0.0.1:27103", Guid.NewGuid());
                Check(reopened.Scale(true).Retired.Length == 0, "a retired node that registers again is no longer retired");
            }
            // A retiring node recovers nothing from a dead peer; a non-retiring survivor takes it all.
            string recoveryDirectory = directory + "-recovery";
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            try
            {
                using var registry = new GatewayRegistry(recoveryDirectory, Admission, () => now, TimeSpan.FromSeconds(10));
                NodeRegistration one = registry.RegisterNode(Join("node-1", "one", Guid.NewGuid()));
                NodeRegistration two = registry.RegisterNode(Join("node-2", "two", Guid.NewGuid()));
                NodeRegistration three = registry.RegisterNode(Join("node-3", "three", Guid.NewGuid()));
                foreach (NodeRegistration n in new[] { one, two, three }) registry.Heartbeat(n.Node, n.Epoch, []);
                PartitionOwnership lost = registry.ClaimPartition(7, one.Node, one.Epoch, 0);
                registry.RegisterSave(new SaveId(7, lost.Generation, 1), one.Node, one.Epoch, SaveKind.Periodic, 1, "c7", 1, "node-1/7", entities: 3);
                registry.SetScaleTarget(1, force: true);
                registry.BeginRetiring(["node-2"], "test");
                now += TimeSpan.FromSeconds(6);
                registry.Heartbeat(two.Node, two.Epoch, []);
                registry.Heartbeat(three.Node, three.Epoch, []);
                now += TimeSpan.FromSeconds(5);
                registry.ExpireLeases();
                Check(registry.ClaimRecoveries(two.Node, two.Epoch, []).Length == 0, "a retiring node recovers nothing from a dead peer");
                Check(registry.ClaimRecoveries(three.Node, three.Epoch, []).Select(x => x.Ownership.Partition).SequenceEqual([7UL]),
                    "a non-retiring survivor recovers the dead peer's partition");
            }
            finally { try { Directory.Delete(recoveryDirectory, true); } catch { } }
            Check(DirectGateway.DrainTarget([Info(1, 0, 0, 0, 10)],
                [("node-1", Report(Info(2, 50_000, 0, 0, 10))), ("node-2", Report(Info(3, 500, 0, 0, 10)))], 4000,
                new Dictionary<string, double> { ["node-1"] = 1, ["node-2"] = 9 }) == "node-2",
                "a component drains to the node already holding its envelope neighbour");
            Check(DirectGateway.DrainTarget([Info(1, 0, 0, 0, 10)],
                [("node-1", Report(Info(2, 50_000, 0, 0, 10))), ("node-2", Report(Info(3, 90_000, 0, 0, 10)))], 4000,
                new Dictionary<string, double> { ["node-1"] = 5, ["node-2"] = 1 }) == "node-2",
                "without neighbours, a component drains to the least loaded receiver");
            Console.WriteLine("node-scaling: target persisted, retiring nodes take no new content, supersede, settle to retired, drain target choice");
        }
        finally { try { Directory.Delete(directory, true); } catch { } }

        static PartitionInfo Info(ulong id, double x, double y, double z, double radius) =>
            new(id, 1, false, new PartitionSphere(x, y, z, radius), 0, []);
        static PartitionReport Report(params PartitionInfo[] partitions) => new(3000, 1000, partitions, 0);
    }

    /// <summary>
    /// T-0296 r3: a rebalance is initiated only once every component is co-located (Docs/GlobalLoadBalancing.md), and
    /// the loop read "ChooseMove chose nothing" as that. It also chooses nothing while the mover is in handover
    /// backoff; the per-node planner then pinned the other half of the split component to a third node.
    /// </summary>
    private static void TestBalanceWaitsForColocation()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-waits-for-colocation: " + what); }
        static PartitionInfo At(ulong id, double x, int pcu) => new(id, 1, true, new PartitionSphere(x, 0, 0, 100), 0, [], pcu, 400L * pcu + 10_000);
        static PartitionReport Report(params PartitionInfo[] partitions) => new(3000, 1000, partitions, 0, 50);
        // 1 on n1 sits inside the envelope of 21 and 23 on n2 (which are out of each other's): 1 has to join them, and
        // 1 is in handover backoff (its capture keeps being refused). 2 is n1's own load; n3 is nearly empty.
        PartitionReport n1 = Report(At(1, 0, 50), At(2, 900_000, 1500)), n2 = Report(At(21, 3500, 1000), At(23, -3500, 1000)),
            n3 = Report(At(31, -900_000, 50));
        bool held = false;
        bool Backoff(ulong id) { if (id != 1) return false; held = true; return true; }
        DirectGateway.Move? unblocked = DirectGateway.ChooseMove(n1, n2, 4000, [], _ => false);
        Check(unblocked is { Moving.Id: 1, Reason: "co-location" }, "the scenario: partition 1 has to move to its neighbours on n2: " + unblocked);
        DirectGateway.Move? move = DirectGateway.ChooseMove(n1, n2, 4000, [], Backoff);
        Check(move == null && held, "with the mover in backoff no move is chosen, and the mover was the reason");
        // Control: what the planner does when it is then allowed to run - it pins a half of the split component away.
        DirectGateway.RebalancePlan[] plans = DirectGateway.ChooseRebalancePlans([(n1, "n1"), (n2, "n2"), (n3, "n3")], new CostModel(), [],
            id => id == 1, fullPlan: true);
        Check(plans.Any(plan => plan.Target == "n3" && plan.Members.Any(member => member.Id is 21 or 23)),
            "control: the planner pins a neighbour (21 or 23) of the held mover to a third node: "
            + string.Join("; ", plans.Select(plan => $"[{string.Join(",", plan.Members.Select(member => member.Id))}] {plan.Source}->{plan.Target}")));
        Check(!DirectGateway.RebalanceMayStart(moveChosen: false, moverHeld: held), "no rebalance starts while a co-location mover is only held by backoff");
        Check(DirectGateway.RebalanceMayStart(moveChosen: false, moverHeld: false), "a co-located cluster may rebalance");
        Check(!DirectGateway.RebalanceMayStart(moveChosen: true, moverHeld: false), "a chosen co-location move preempts the rebalance, as before");
        Console.WriteLine("balance-waits-for-colocation: a mover in handover backoff keeps the balancer from pinning its split component apart");
    }

    /// <summary>
    /// T-0337: offline wakes and recoveries that no co-located owner dictates go to the serving node with the fewest
    /// partitions on that host. A node promoted by a scale-out stayed Serving with 0 partitions for 20+ min: free
    /// arrivals went to the lowest projected load or to whichever node asked first. Invariants: a wake caused by an
    /// approach stays with the approaching node; directed, reserved and warm claims are never held back; a recovery is
    /// not starved by an emptier node that never asks.
    /// </summary>
    private static void TestFewestPartitionsPlacement()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("fewest-partitions-placement: " + what); }
        // The decision functions.
        Check(GatewayRegistry.FewestPartitionsNode([("node-1", 9, 10), ("node-2", 0, 500), ("node-3", 4, 0)]) == "node-2",
            "the fewest partitions win over a lower load");
        Check(GatewayRegistry.FewestPartitionsNode([("node-3", 2, 70), ("node-2", 2, 30), ("node-1", 2, 30)]) == "node-1",
            "ties: lowest load, then node id");
        Check(GatewayRegistry.FewestPartitionsNode([]) == null, "no eligible node, no target");
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        TimeSpan patience = TimeSpan.FromSeconds(5);
        Check(GatewayRegistry.RecoveryClaimWaitsFor(3, [("node-2", 0, 0, null), ("node-3", 1, 0, null)], t0, patience) == "node-2",
            "an asker waits for the emptiest peer");
        Check(GatewayRegistry.RecoveryClaimWaitsFor(3, [("node-2", 3, 0, null), ("node-3", 7, 0, null)], t0, patience) == null,
            "no peer with fewer partitions: the asker claims (a tie does not wait)");
        Check(GatewayRegistry.RecoveryClaimWaitsFor(3, [("node-2", 0, 0, t0)], t0 + TimeSpan.FromSeconds(4), patience) == "node-2",
            "a peer waited for less than the patience still comes first");
        Check(GatewayRegistry.RecoveryClaimWaitsFor(3, [("node-2", 0, 0, t0)], t0 + patience, patience) == null,
            "no starvation: a peer that did not claim within the patience is not waited for");
        Check(GatewayRegistry.RecoveryClaimWaitsFor(3, [("node-2", 0, 0, t0), ("node-3", 1, 0, null)], t0 + patience, patience) == "node-3",
            "the next emptier peer is waited for in its turn");

        string directory = Directory.CreateTempSubdirectory("gw-selftest-").FullName;
        TextWriter console = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            NodeRegistration Boot(GatewayRegistry registry, string node, string? host) => registry.RegisterNode(new NodeJoinRequest(node, "ep-" + node,
                Guid.NewGuid(), CurrentToken, "cluster", "world", "config", "binary", "types", "settings", 3000, Host: host));
            // Offline wakes. full and empty share host h (which holds the saves); far is on host g, as empty as empty
            // and with the lowest projected load - the df pick.
            using (var registry = new GatewayRegistry(Path.Combine(directory, "wake"), Admission, () => now, TimeSpan.FromSeconds(10)))
            {
                NodeRegistration full = Boot(registry, "full", "h"), empty = Boot(registry, "empty", "h"), far = Boot(registry, "far", "g");
                registry.Heartbeat(full.Node, full.Epoch, [], projectedLoad: 10);
                registry.Heartbeat(empty.Node, empty.Epoch, [], projectedLoad: 50);
                registry.Heartbeat(far.Node, far.Epoch, [], projectedLoad: 0);
                Dictionary<ulong, long> generation = [];
                foreach (ulong partition in new ulong[] { 1, 2, 3, 7, 8, 9 })
                {
                    generation[partition] = registry.ClaimPartition(partition, full.Node, full.Epoch, 0).Generation;
                    registry.RegisterSave(new SaveId(partition, generation[partition], 1), full.Node, full.Epoch, SaveKind.Final, 1, "c" + partition, 2,
                        "full/" + partition, "http://full/control");
                }
                foreach (ulong partition in new ulong[] { 7, 8, 9 })
                    registry.TakeOffline(new OfflineRequest(partition, full.Node, full.Epoch, generation[partition], 1, 100, 0, 0, 50, [[100, 0, 0, 50]],
                        [(long)partition * 10], 1000, "[]", "[]"));
                // Invariant: a wake caused by an approach lands next to the approaching partition, however full its node.
                string approach = registry.RequestOfflineLoad(8, full.Node, "approach");
                Check(approach == "requested:full", "an approach wake still goes to the approaching node: " + approach);
                string join = registry.RequestOfflineLoad(7, null, "join");
                Check(join == "requested:empty", "a join wake goes to the node with the fewest partitions on the host holding the save: " + join);
                Check(registry.RequestOfflineLoad(7, null, "join") == "requested:empty", "re-asserted demand keeps its target");
                // The directed load on its way to empty counts: with far gone, the next free wake... still empty (1 vs 4).
                registry.DrainingNodes = () => [full.Node];
                string fallback = registry.RequestOfflineLoad(9, full.Node, "approach");
                Check(fallback == "requested:empty", "a wake whose node cannot take it goes to the emptiest node of that host: " + fallback);
                registry.DrainingNodes = () => [full.Node, empty.Node];
                now += TimeSpan.FromSeconds(61);   // the requests above lapse
                foreach (NodeRegistration n in new[] { empty, far }) registry.Heartbeat(n.Node, n.Epoch, [], liveness: true);
                registry.Heartbeat(full.Node, full.Epoch, [], liveness: true);
                string elsewhere = registry.RequestOfflineLoad(7, null, "join");
                Check(elsewhere == "requested:far", "no node of that host can take it: any eligible node: " + elsewhere);
                registry.DrainingNodes = null;
            }
            // No host notion: among all eligible nodes.
            using (var registry = new GatewayRegistry(Path.Combine(directory, "hostless"), Admission, () => now, TimeSpan.FromSeconds(10)))
            {
                NodeRegistration full = Boot(registry, "full", null), light = Boot(registry, "light", null), empty = Boot(registry, "empty", null);
                registry.Heartbeat(full.Node, full.Epoch, [], projectedLoad: 0);
                registry.Heartbeat(light.Node, light.Epoch, [], projectedLoad: 5);
                registry.Heartbeat(empty.Node, empty.Epoch, [], projectedLoad: 50);
                registry.ClaimPartition(1, light.Node, light.Epoch, 0);
                registry.ClaimPartition(2, full.Node, full.Epoch, 0);
                long seven = registry.ClaimPartition(7, full.Node, full.Epoch, 0).Generation;
                registry.RegisterSave(new SaveId(7, seven, 1), full.Node, full.Epoch, SaveKind.Final, 1, "c7", 2, "full/7", "http://full/control");
                registry.TakeOffline(new OfflineRequest(7, full.Node, full.Epoch, seven, 1, 100, 0, 0, 50, [[100, 0, 0, 50]], [70], 1000, "[]", "[]"));
                string join = registry.RequestOfflineLoad(7, null, "join");
                Check(join == "requested:empty", "without hosts the fewest partitions among all eligible nodes win: " + join);
            }
            // Recoveries: dead held 1..6 and the offline 20; busy holds 11..13; idle (same host) and far (host g) hold nothing.
            (GatewayRegistry Registry, NodeRegistration Busy, NodeRegistration Idle, NodeRegistration Far, PartitionVersion[] Held) Lost(string name)
            {
                var registry = new GatewayRegistry(Path.Combine(directory, name), Admission, () => now, TimeSpan.FromSeconds(10));
                NodeRegistration dead = Boot(registry, "dead", "h"), busy = Boot(registry, "busy", "h"), idle = Boot(registry, "idle", "h"),
                    far = Boot(registry, "far", "g");
                foreach (NodeRegistration n in new[] { dead, busy, idle, far }) registry.Heartbeat(n.Node, n.Epoch, []);
                foreach (ulong partition in new ulong[] { 1, 2, 3, 4, 5, 6, 20 })
                {
                    long generation = registry.ClaimPartition(partition, dead.Node, dead.Epoch, 0).Generation;
                    registry.RegisterSave(new SaveId(partition, generation, 1), dead.Node, dead.Epoch, SaveKind.Final, 1, "c" + partition, 2,
                        "dead/" + partition, entities: 3);
                    if (partition == 20)
                        registry.TakeOffline(new OfflineRequest(20, dead.Node, dead.Epoch, generation, 1, 100, 0, 0, 50, [[100, 0, 0, 50]], [200], 1000, "[]", "[]"));
                }
                PartitionVersion[] held = new ulong[] { 11, 12, 13 }
                    .Select(partition => new PartitionVersion(partition, registry.ClaimPartition(partition, busy.Node, busy.Epoch, 0).Generation, 3)).ToArray();
                now += TimeSpan.FromSeconds(6);
                registry.Heartbeat(busy.Node, busy.Epoch, held);
                foreach (NodeRegistration n in new[] { idle, far }) registry.Heartbeat(n.Node, n.Epoch, []);
                now += TimeSpan.FromSeconds(5);
                registry.ExpireLeases();
                return (registry, busy, idle, far, held);
            }
            var a = Lost("recover");
            using (a.Registry)
            {
                Check(a.Registry.RecoveryPending(), "the scenario: the dead node's partitions wait for recovery");
                Check(a.Registry.ClaimRecoveries(a.Busy.Node, a.Busy.Epoch, []).Length == 0, "a node with 3 partitions waits while an empty node of its host can claim");
                Check(a.Registry.ClaimRecoveries(a.Far.Node, a.Far.Epoch, []).Length == 1, "an empty node (of any host) claims at once");
                for (int i = 1; i <= 3; i++)
                {
                    Check(a.Registry.ClaimRecoveries(a.Busy.Node, a.Busy.Epoch, []).Length == 0, $"the fuller node still waits before the emptier one's claim {i}");
                    Check(a.Registry.ClaimRecoveries(a.Idle.Node, a.Idle.Epoch, []).Length == 1, $"the emptier node's claim {i} is granted");
                }
                Check(a.Registry.ClaimRecoveries(a.Busy.Node, a.Busy.Epoch, []).Length == 1, "level with its host's emptiest node, the asker claims (a tie does not wait)");
            }
            var b = Lost("starve");
            using (b.Registry)
            {
                // Invariants first: what is not free is never held back, although idle is emptier.
                Check(b.Registry.RequestOfflineLoad(20, b.Busy.Node, "approach") == "requested:busy", "the approach wake is aimed at the approaching node");
                Check(b.Registry.ClaimRecoveries(b.Busy.Node, b.Busy.Epoch, []).Select(x => x.Ownership.Partition).SequenceEqual([20UL]),
                    "a directed load is claimed by its target although an emptier node exists");
                Check(b.Registry.ClaimRecoveries(b.Busy.Node, b.Busy.Epoch, [1]).Select(x => x.Ownership.Partition).SequenceEqual([1UL]),
                    "a warm node's own list is not held back either");
                // No starvation: idle never asks.
                Check(b.Registry.ClaimRecoveries(b.Busy.Node, b.Busy.Epoch, []).Length == 0, "the free pick waits for the emptier node");
                now += TimeSpan.FromSeconds(4);
                b.Registry.Heartbeat(b.Busy.Node, b.Busy.Epoch, [], liveness: true);
                b.Registry.Heartbeat(b.Idle.Node, b.Idle.Epoch, []);
                Check(b.Registry.ClaimRecoveries(b.Busy.Node, b.Busy.Epoch, []).Length == 0, "and still waits 4 s later");
                now += TimeSpan.FromSeconds(1);
                Check(b.Registry.ClaimRecoveries(b.Busy.Node, b.Busy.Epoch, []).Length == 1, "no starvation: after 5 s without a claim by the emptiest node, the asker claims");
                Check(b.Registry.ClaimRecoveries(b.Busy.Node, b.Busy.Epoch, []).Length == 1, "and keeps claiming without a new wait per partition");
                // The emptier node turns up after all: it is granted, and comes first again from then on.
                Check(b.Registry.ClaimRecoveries(b.Idle.Node, b.Idle.Epoch, []).Length == 1, "a late emptier node is granted its claim");
                Check(b.Registry.ClaimRecoveries(b.Busy.Node, b.Busy.Epoch, []).Length == 0, "and is waited for again once it claims");
            }
        }
        finally
        {
            Console.SetOut(console);
            try { Directory.Delete(directory, true); } catch { }
        }
        Console.WriteLine("fewest-partitions-placement: free offline wakes and recoveries go to the emptiest serving node of the host; approach wakes and directed claims unchanged; no starvation");
    }

    /// <summary>
    /// T-0296 r4: one serving node that does not answer /partitions ended the automatic pass for every node. The pass
    /// now goes on with the nodes that reported, and whatever reads an ABSENCE waits for a full pass.
    /// </summary>
    private static void TestAutomaticPassPartial()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("automatic-pass-partial: " + what); }
        static PartitionInfo At(ulong id, double x, params ulong[] clients) => new(id, 1, true, new PartitionSphere(x, 0, 0, 100), 0, clients, 100, 50_000);
        static PartitionReport Report(params PartitionInfo[] partitions) => new(3000, 1000, partitions, 0);
        // n1 holds 1, n3 holds 3 inside its envelope: 1 and 3 have to be co-located. n2 (partition 2, client 77) does not answer.
        Dictionary<string, PartitionReport> nodes = new() { ["n1"] = Report(At(1, 0)), ["n3"] = Report(At(3, 500)) };
        PartitionReport Fetch(string node) => nodes.TryGetValue(node, out PartitionReport? report) ? report
            : throw new HttpRequestException("Connection refused (n2:29417)");
        string[] served = ["n1", "n2", "n3"];
        bool passLost = false;
        try { _ = served.Select(node => (node, Fetch(node))).ToArray(); }
        catch (HttpRequestException) { passLost = true; }
        Check(passLost, "control: the df collection throws at the silent node, and the pass ends before any wake or co-location");
        List<(string Node, string Error)> silent = [];
        (string Node, PartitionReport Report)[] reports = DirectGateway.CollectReports(served, Fetch, silent);
        Check(reports.Select(x => x.Node).SequenceEqual(["n1", "n3"]) && silent.Count == 1 && silent[0].Node == "n2" && silent[0].Error.Contains("refused"),
            "the pass keeps the reports it got and names the silent node: " + string.Join(",", reports.Select(x => x.Node)));
        Check(DirectGateway.ChooseMove(reports[0].Report, reports[1].Report, 4000, [], _ => false) is { Reason: "co-location" },
            "the nodes that reported are still co-located");
        silent.Clear();
        Check(DirectGateway.CollectReports(served, node => node == "n2" ? throw new TaskCanceledException("timeout") : Fetch(node), silent).Length == 2
            && silent.Count == 1, "a node whose game thread does not answer inside the timeout is silent too");
        bool malformed = false;
        try { DirectGateway.CollectReports(served, node => node == "n2" ? throw new InvalidDataException("Empty partition report") : Fetch(node), silent); }
        catch (InvalidDataException) { malformed = true; }
        Check(malformed, "a malformed report still fails the pass: only the transport is excused");
        Check(DirectGateway.MissingReportPause([]) == null, "a full pass plans as before");
        Check(DirectGateway.MissingReportPause(["n2"]) is { } pause && pause.Contains("n2"), "a partial pass plans no rebalance and says which node is silent");
        // Why the pin prune waits: partition 2 (on the silent n2) and a pin toward n2 would both read as gone.
        DateTime now = new(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc);
        Dictionary<ulong, (string Target, DateTime Expires)> pins = new() { [2] = ("n3", now.AddSeconds(60)), [1] = ("n2", now.AddSeconds(60)) };
        Check(DirectGateway.PruneMigrationPins(new(pins), new HashSet<ulong> { 1, 3 }, new HashSet<string> { "n1", "n3" }, now).Count == 2,
            "control: a prune over partial reports drops the pins of and toward the silent node");
        // Why the route clock waits: client 77 is routed to partition 2, which only the silent n2 would report.
        (ulong Client, ulong Partition)[] routes = [(77, 2)];
        PartitionInfo[] listed = [.. reports.SelectMany(x => x.Report.Partitions)];
        Dictionary<ulong, DateTimeOffset> contradicted = [];
        DateTimeOffset at = new(now);
        DirectGateway.RoutedClientBearing(routes, DirectGateway.ClientBearingAsReported(listed, routes, partial: false), contradicted, at, TimeSpan.FromSeconds(120), out _);
        Check(contradicted.ContainsKey(2), "control: read as a full pass, the silent node's client route starts counting as contradicted by every report");
        contradicted.Clear();
        HashSet<ulong> routed = DirectGateway.RoutedClientBearing(routes, DirectGateway.ClientBearingAsReported(listed, routes, partial: true), contradicted,
            at, TimeSpan.FromSeconds(120), out _);
        Check(routed.Contains(2) && contradicted.Count == 0, "on a partial pass a routed partition no report lists stays client-bearing and uncontradicted");
        Check(DirectGateway.ClientBearingAsReported([At(1, 0), At(2, 0, 77)], routes, partial: false).SetEquals(new ulong[] { 2 })
            && !DirectGateway.ClientBearingAsReported([At(1, 0), At(2, 0)], routes, partial: true).Contains(2),
            "a listed partition is judged by its own report, partial pass or not");
        Console.WriteLine("automatic-pass-partial: a silent node no longer ends the pass; prune, scale-in, planner, route clock and snapshot wait for a full one");
    }

    private static void TestBalancePinLifecycle()
    {
        // T-0311 (race review F6): balance-migration pins outlived their partitions and targets. Any
        // member's arrival renewed EVERY pin, expired ones included (expiry was only enforced lazily
        // when a reported partition was queried), and nothing removed the pin of a partition no node
        // reports any more (split or merged away, owner dead) or of a target that no longer serves.
        // Such a pin kept its target "busy" for the paced planner and kept a half-moved group split.
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("balance-pin-lifecycle: " + what); }
        DateTime now = new(2026, 9, 30, 19, 0, 0, DateTimeKind.Utc);
        Dictionary<ulong, (string Target, DateTime Expires)> pins = new()
        {
            [1] = ("n2", now.AddSeconds(60)),    // live pin, reported, target serving: kept
            [2] = ("n2", now.AddSeconds(-5)),    // expired, not yet queried
            [3] = ("n2", now.AddSeconds(60)),    // partition split away: no node reports it
            [4] = ("n9", now.AddSeconds(60)),    // target closed / dead / spare
        };
        DirectGateway.RenewMigrationPins(pins, now, 180);
        Check(pins[1].Expires == now.AddSeconds(180), "a live pin is renewed on progress");
        Check(!pins.ContainsKey(2), "an expired pin is dropped, not revived, on progress");
        List<string> dropped = DirectGateway.PruneMigrationPins(pins, reported: new HashSet<ulong> { 1, 4 },
            serving: new HashSet<string> { "n1", "n2" }, now);
        Check(pins.Keys.Order().SequenceEqual(new ulong[] { 1 }), "only the live pin survives the prune: " + string.Join(",", pins.Keys));
        Check(dropped.Count == 2 && dropped.Any(x => x.Contains("partition 3") && x.Contains("no node reports it"))
            && dropped.Any(x => x.Contains("partition 4") && x.Contains("n9")), "each dropped pin is logged with its reason: " + string.Join(" | ", dropped));
        pins[5] = ("n1", now.AddSeconds(-1));
        Check(DirectGateway.PruneMigrationPins(pins, new HashSet<ulong> { 1, 5 }, new HashSet<string> { "n1", "n2" }, now).Count == 0
            && !pins.ContainsKey(5), "an expired pin is pruned silently");
        // T-0296 r2: node control drains a node through Retiring (remove/recycle/decrease) while it still serves and reports.
        // A pin made before its stop toward it must end, or the co-location loop carries members INTO the draining node and
        // the scale-in pass pins them straight out again. Red arm = the pass before the fix (retiring not consulted).
        Dictionary<ulong, (string Target, DateTime Expires)> Train() => new() { [7] = ("n3", now.AddSeconds(60)), [8] = ("n2", now.AddSeconds(60)) };
        var reportedTrain = new HashSet<ulong> { 7, 8 };
        var servingTrain = new HashSet<string> { "n1", "n2", "n3" };
        Dictionary<ulong, (string Target, DateTime Expires)> before = Train();
        DirectGateway.PruneMigrationPins(before, reportedTrain, servingTrain, now);
        Check(before.ContainsKey(7), "red arm: without the retiring set the pin toward the draining n3 survives");
        Dictionary<ulong, (string Target, DateTime Expires)> after = Train();
        List<string> droppedRetiring = DirectGateway.PruneMigrationPins(after, reportedTrain, servingTrain, now, new HashSet<string> { "n3" });
        Check(!after.ContainsKey(7) && after.ContainsKey(8), "a pin toward a retiring target is dropped, others kept: " + string.Join(",", after.Keys));
        Check(droppedRetiring.Count == 1 && droppedRetiring[0].Contains("n3 is retiring"), "the drop names the retiring target: " + string.Join(" | ", droppedRetiring));
        // T-0296 r3: a pull demand moved a pinned partition to the demanding node and left the pin in place. What the
        // next pass then does with it is the defect: the pair (demander n2, pin target n3) carries it off n2 again.
        static PartitionInfo Pulled(ulong id, double x) => new(id, 1, true, new PartitionSphere(x, 0, 0, 100), 0, [], 100, 50_000);
        static PartitionReport Node(params PartitionInfo[] partitions) => new(3000, 1000, partitions, 0);
        DirectGateway.Move? undone = DirectGateway.ChooseMove(Node(Pulled(9, 0)), Node(Pulled(20, 900_000)), 4000, [], _ => false,
            migration: id => id == 9 ? DirectGateway.MigrationSide.Target : DirectGateway.MigrationSide.None);
        Check(undone is { Moving.Id: 9, ToTarget: true, Reason: "balance-migration" },
            "control: a pulled partition still pinned elsewhere is carried off the demanding node on the next pass");
        Check(DirectGateway.PullPinRefusal("n3", "n2") is { } refusal && refusal.Contains("n3"),
            "a pull demand of a partition pinned to another node is refused, naming the pin's target");
        Check(DirectGateway.PullPinRefusal("n2", "n2") == null, "a pull toward the pin's own target is the migration's move");
        Check(DirectGateway.PullPinRefusal(null, "n2") == null, "an unpinned partition is pulled as before");
        // T-0296 r4: a validation block (T-0249) made ChooseMove ignore the pin, and left the pin. Group {11, 12} n1 -> n2:
        // 11 arrived (its pin released), 12 was refused by validation and is HandoverBlocked.
        Dictionary<ulong, (string Target, DateTime Expires)> group = new() { [12] = ("n2", now.AddSeconds(120)), [40] = ("n2", now.AddSeconds(120)) };
        DirectGateway.MigrationSide Side(ulong id) => group.ContainsKey(id) ? DirectGateway.MigrationSide.Target : DirectGateway.MigrationSide.None;
        HashSet<ulong> voided = [];
        DirectGateway.Move? reunite = DirectGateway.ChooseMove(Node(Pulled(12, 0)), Node(Pulled(11, 500)), 4000, [], _ => false,
            validationBlocked: id => id == 12, migration: Side, pinVoided: voided.UnionWith);
        Check(reunite is { Moving.Id: 11, ToTarget: false, Reason: "co-location" }, "while 12 is blocked the arrived member is brought back to it: " + reunite);
        // Control: the block has lapsed, 11 is back beside 12, and the pin of 12 is still there.
        DirectGateway.Move? bounce = DirectGateway.ChooseMove(Node(Pulled(12, 0), Pulled(11, 500)), Node(), 4000, [], _ => false, migration: Side);
        Check(bounce is { Moving.Id: 12, ToTarget: true, Reason: "balance-migration" }, "control: the surviving pin carries the reunited group apart again: " + bounce);
        Check(voided.SetEquals(new ulong[] { 11, 12 }), "the pass names the group whose pin it did not honour: " + string.Join(",", voided));
        List<string> droppedVoided = DirectGateway.DropVoidedPins(group, voided);
        Check(droppedVoided.Count == 1 && droppedVoided[0].Contains("partition 12") && group.ContainsKey(40), "that group's pins are dropped, other trains kept: " + string.Join(" | ", droppedVoided));
        Check(DirectGateway.ChooseMove(Node(Pulled(12, 0), Pulled(11, 500)), Node(), 4000, [], _ => false, migration: Side) == null,
            "after the block lapses the reunited group stays where it is");
        // T-0296 r4: the T-0249 stall. Group {21, 22, 23} n1 -> n2: 21 arrived, 23 is in handover backoff (its capture keeps
        // being refused), so 22 waits. The stall's exit is the pin expiring - which another train's progress prevented.
        Dictionary<ulong, (string Target, DateTime Expires)> stall = new()
            { [22] = ("n2", now.AddSeconds(30)), [23] = ("n2", now.AddSeconds(30)), [40] = ("n3", now.AddSeconds(30)) };
        DirectGateway.MigrationSide StallSide(ulong id) => stall.TryGetValue(id, out var pin) && pin.Target == "n2" ? DirectGateway.MigrationSide.Target
            : stall.ContainsKey(id) ? DirectGateway.MigrationSide.Elsewhere : DirectGateway.MigrationSide.None;
        PartitionReport stallSource = Node(Pulled(22, 0), Pulled(23, 500)), stallTarget = Node(Pulled(21, 1000));
        List<ulong[]> trains = [];
        Check(DirectGateway.ChooseMove(stallSource, stallTarget, 4000, [], id => id == 23, migration: StallSide, carried: trains.Add) == null
            && trains.Count == 1 && trains[0].Order().SequenceEqual(new ulong[] { 22, 23 }),
            "the stalled train moves nothing and names the members it still has to carry: " + string.Join(" | ", trains.Select(t => string.Join(",", t))));
        // Control: train 40 arrives somewhere 20 s later and, as on df, renews every live pin.
        Dictionary<ulong, (string Target, DateTime Expires)> revived = new(stall);
        DirectGateway.RenewMigrationPins(revived, now.AddSeconds(20), 180);
        Check(revived[22].Expires == now.AddSeconds(200), "control: another train's progress renews the stalled group's pins, so the stall never expires");
        HashSet<ulong> stalledPins = DirectGateway.StalledMigrationPins(trains, id => id == 23);
        Check(stalledPins.SetEquals(new ulong[] { 22, 23 }), "a train with a failing mover is stalled as a whole: " + string.Join(",", stalledPins));
        DirectGateway.RenewMigrationPins(stall, now.AddSeconds(20), 180, stalledPins);
        Check(stall[22].Expires == now.AddSeconds(30) && stall[23].Expires == now.AddSeconds(30) && stall[40].Expires == now.AddSeconds(200),
            "another train's progress renews its own pins and not the stalled train's");
        DirectGateway.PruneMigrationPins(stall, new HashSet<ulong> { 21, 22, 23, 40 }, new HashSet<string> { "n1", "n2", "n3" }, now.AddSeconds(31));
        Check(!stall.ContainsKey(22) && !stall.ContainsKey(23) && stall.ContainsKey(40), "the stalled train's pins expire: " + string.Join(",", stall.Keys));
        DirectGateway.Move? reunited = DirectGateway.ChooseMove(stallSource, stallTarget, 4000, [], id => id == 23, migration: StallSide);
        Check(reunited is { Moving.Id: 21, ToTarget: false, Reason: "co-location" }, "then cost-based co-location reunites the group: " + reunited);
        Check(DirectGateway.StalledMigrationPins(trains, _ => false).Count == 0, "the mover's success ends the stall: its train is renewed again");
        // T-0296 r4: fast is checked when the planner pins and never again. Train {51, 52} n1 -> n2; 52 turned fast after the pin.
        Dictionary<ulong, (string Target, DateTime Expires)> fastTrain = new() { [51] = ("n2", now.AddSeconds(60)), [52] = ("n2", now.AddSeconds(60)) };
        DirectGateway.MigrationSide FastSide(ulong id) => fastTrain.ContainsKey(id) ? DirectGateway.MigrationSide.Target : DirectGateway.MigrationSide.None;
        PartitionReport fastSource = Node(Pulled(52, 500) with { Fast = true, FastSpeed = 1500 }), fastTarget = Node(Pulled(51, 0));
        var fastReported = new HashSet<ulong> { 51, 52 };
        var fastServing = new HashSet<string> { "n1", "n2" };
        DirectGateway.PruneMigrationPins(fastTrain, fastReported, fastServing, now);
        Check(DirectGateway.ChooseMove(fastSource, fastTarget, 4000, [], _ => false, migration: FastSide) is { Moving.Id: 52, Reason: "balance-migration" },
            "control: the df prune keeps the pin and the fast member is handed over as a balance migration");
        List<string> droppedFast = DirectGateway.PruneMigrationPins(fastTrain, fastReported, fastServing, now, fast: new HashSet<ulong> { 52 });
        Check(droppedFast.Count == 1 && droppedFast[0].Contains("partition 52") && droppedFast[0].Contains("fast") && fastTrain.ContainsKey(51),
            "the pin of a member that turned fast is dropped, its siblings' kept: " + string.Join(" | ", droppedFast));
        Check(DirectGateway.ChooseMove(fastSource, fastTarget, 4000, [], _ => false, migration: FastSide) == null,
            "the fast member stays with its owner (isolated, as in co-location)");
        Check(DirectGateway.ChooseMove(Node(Pulled(52, 500)), fastTarget, 4000, [], _ => false, migration: FastSide) is { Moving.Id: 52, ToTarget: true },
            "once slow it follows its group (by the siblings' pin while that lives, else by co-location)");
        Console.WriteLine("balance-pin-lifecycle: expired pins are not revived; pins of unreported partitions and non-serving targets are pruned");
    }

    private static void TestTopologyReplayAfterClose()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-self-test-" + Guid.NewGuid());
        try
        {
            using var registry = new GatewayRegistry(directory, Admission);
            Guid instance = Guid.NewGuid();
            NodeRegistration node = Register(registry, "n1", "127.0.0.1:27016", instance);
            PartitionOwnership parent = registry.ClaimPartition(60, node.Node, node.Epoch, 0);
            PartitionOwnership other = registry.ClaimPartition(61, node.Node, node.Epoch, 0);
            ulong[] outputs = registry.AllocatePartitionIds(node.Node, node.Epoch, 2);
            PartitionVersion input = new(60, parent.Generation);
            Dictionary<ulong, long[]> members = new() { [outputs[0]] = [1], [outputs[1]] = [2] };
            LineageRecord committed = registry.ApplyLineage("lost-reply", LineageKind.Split, node.Node, node.Epoch, [input], outputs, members, 5);
            registry.CloseNode(node.Node, node.Epoch, TimeSpan.FromMinutes(1));
            LineageRecord replayed = registry.ApplyLineage("lost-reply", LineageKind.Split, node.Node, node.Epoch, [input], outputs, members, 5);
            Assert(replayed.Outputs.SequenceEqual(committed.Outputs));
            Assert(committed.Outputs.All(x => registry.GetPartitionOwner(x.Partition).Node == node.Node));
            AssertConflict(() => registry.ApplyLineage("lost-reply", LineageKind.Split, node.Node, node.Epoch, [input], outputs.Reverse().ToArray(), members, 5));
            AssertConflict(() => registry.ApplyLineage("new-after-close", LineageKind.Merge, node.Node, node.Epoch,
                [new(61, other.Generation)], [61], null, 6));
            // An old incarnation's replay is fenced by the epoch, even for a recorded op id.
            NodeRegistration again = Register(registry, "n1", "127.0.0.1:27016", Guid.NewGuid());
            Assert(again.Epoch != node.Epoch);
            AssertConflict(() => registry.ApplyLineage("lost-reply", LineageKind.Split, node.Node, node.Epoch, [input], outputs, members, 5));
            Console.WriteLine("topology-replay-after-close: exact replay by the Closed writer returned the committed outputs; differing, new and old-incarnation requests refused");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// T-0354: what the node's asynchronous apply relies on. It no longer claims a split's output ids (they come
    /// allocated from its reserve), it repeats an unanswered request unchanged, and while the answer is out its
    /// heartbeat reports the ownership the request makes beside the ownership it knows.
    /// </summary>
    private static void TestAsyncApplyContract()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-self-test-" + Guid.NewGuid());
        try
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            using var registry = new GatewayRegistry(directory, Admission, () => now, TimeSpan.FromSeconds(10));
            NodeRegistration node = Register(registry, "n1", "127.0.0.1:27016", Guid.NewGuid());
            PartitionOwnership parent = registry.ClaimPartition(70, node.Node, node.Epoch, 0);
            ulong[] reserve = registry.AllocatePartitionIds(node.Node, node.Epoch, 3);
            ulong[] outputs = [reserve[0], reserve[1]];
            ulong stray = reserve[2];
            PartitionVersion input = new(70, parent.Generation);
            Dictionary<ulong, long[]> members = new() { [outputs[0]] = [1], [outputs[1]] = [2] };
            // What the node reports while a request is queued: the row it knows, and the rows the request will make.
            PartitionVersion[] report = [input, new(outputs[0], 1), new(outputs[1], 1), new(stray, 1)];
            registry.Heartbeat(node.Node, node.Epoch, report);
            foreach (ulong id in reserve)
                AssertArgument(() => registry.GetPartitionOwner(id));   // reporting a row does not make it
            // Outputs that were allocated and never claimed are accepted; the apply makes their rows at generation 1.
            LineageRecord committed = registry.ApplyLineage("t0354-split", LineageKind.Split, node.Node, node.Epoch, [input], outputs, members, 5);
            Assert(committed.Outputs.All(x => x.Generation == 1 && registry.GetPartitionOwner(x.Partition).Node == node.Node));
            // The answer is lost. For longer than a lease the node knows only the retired input, but its heartbeat
            // names the outputs at the generation the apply gives them: their leases must hold, nothing is released.
            for (int second = 0; second < 30; second++)
            {
                now += TimeSpan.FromSeconds(1);
                registry.Heartbeat(node.Node, node.Epoch, report);
                registry.ExpireLeases();
            }
            Assert(outputs.All(id => registry.GetPartitionOwner(id).Node == node.Node && registry.GetPartitionOwner(id).Generation == 1));
            // The same request again is answered as recorded and applies nothing twice.
            int lineage = registry.Lineage().Length;
            LineageRecord replayed = registry.ApplyLineage("t0354-split", LineageKind.Split, node.Node, node.Epoch, [input], outputs, members, 5);
            Assert(replayed.Outputs.SequenceEqual(committed.Outputs) && registry.Lineage().Length == lineage);
            // Falsifier: the same change under a NEW op id (a node that reverted and proposed again) is refused for good.
            AssertConflict(() => registry.ApplyLineage("t0354-split-again", LineageKind.Split, node.Node, node.Epoch, [input], outputs, members, 5));
            // A new layout's claim: the first request expects generation 0. With its answer lost, the same request is a
            // generation conflict - so the node repeats it without the expectation and gets its own row back.
            PartitionOwnership claimed = registry.ClaimPartition(stray, node.Node, node.Epoch, 0);
            Assert(claimed.Generation == 1);
            AssertConflict(() => registry.ClaimPartition(stray, node.Node, node.Epoch, 0));
            PartitionOwnership again = registry.ClaimPartition(stray, node.Node, node.Epoch, null);
            Assert(again.Generation == 1 && again.Node == node.Node && registry.GetPartitionOwner(stray).Generation == 1);
            // Had the first claim never arrived, the repeat makes the row, at the same generation.
            ulong late = registry.AllocatePartitionIds(node.Node, node.Epoch, 1)[0];
            Assert(registry.ClaimPartition(late, node.Node, node.Epoch, null).Generation == 1);
            Console.WriteLine("async-apply-contract: unclaimed allocated ids accepted as split outputs; reported expected rows kept their leases for 30 s; "
                + "op-id replay answered as recorded, a new op id refused; a claim replay without the expectation returned the node's row");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void TestRegistry()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-self-test-" + Guid.NewGuid());
        try
        {
            using (var registry = new GatewayRegistry(directory, Admission))
            {
                Guid instance = Guid.NewGuid();
                NodeRegistration node = Register(registry, "n1", "127.0.0.1:27016", instance);
                Assert(ReferenceEquals(node, Register(registry, "n1", "127.0.0.1:27016", instance)));
                NetworkIdBlock firstBlock = registry.AllocateNetworkIdBlock(node.Node, node.Epoch);
                Assert(firstBlock.Start == 1 && firstBlock.End == 65_536);
                PartitionOwnership owner = registry.ClaimPartition(7, node.Node, node.Epoch, 0);
                SaveRegistration save = registry.RegisterSave("s1", node.Node, node.Epoch, owner.Generation, [7], "/save/one", DateTimeOffset.UnixEpoch);
                // Re-registering the same legacy id is idempotent: one row, same identity, and the
                // caller never gets the live registration back (KI-15). Asserting reference
                // equality here hid the real defect - the legacy id was being stamped on the
                // snapshot the write path returns, so the catalogued row kept LegacyId null and a
                // second row was created on every repeat.
                SaveRegistration again = registry.RegisterSave("s1", node.Node, node.Epoch, owner.Generation, [7], "/save/one", DateTimeOffset.UnixEpoch);
                Assert(!ReferenceEquals(save, again) && again.Id == "s1" && again.SaveId == save.SaveId);
                Assert(registry.Saves().Count(x => x.SaveId.Partition == 7) == 1);
                Assert(registry.NewestSave(7)?.Id == "s1");
                NodeRegistration restarted = Register(registry, "n1", "127.0.0.1:27016", Guid.NewGuid());
                Assert(restarted.Epoch == node.Epoch + 1);
                AssertConflict(() => registry.ClaimPartition(8, node.Node, node.Epoch, 0));

                NodeRegistration target = Register(registry, "n2", "127.0.0.1:27018", Guid.NewGuid());
                TransferGrant saveGrant = registry.IssueGrant(new("save", string.Empty, 7, owner.Generation, 1,
                    target.Node, target.Epoch, restarted.Node));
                registry.ValidateGrant(new(saveGrant.Token, "save", string.Empty, 7, owner.Generation, 1, restarted.Node, restarted.Epoch));
                EntityIdRange firstRange = registry.AllocateEntityIdRange(restarted.Node, restarted.Epoch);
                EntityIdRange secondRange = registry.AllocateEntityIdRange(target.Node, target.Epoch);
                Assert(firstRange.Index == 1 && secondRange.Index == 2 && firstRange.Start == (1L << 24) && firstRange.End == (2L << 24) - 1);
                Assert(firstRange.End < secondRange.Start && (secondRange.End >> 56) == 0);
                AssertConflict(() => registry.AllocateEntityIdRange(node.Node, node.Epoch));
                NetworkIdBlock secondBlock = registry.AllocateNetworkIdBlock(target.Node, target.Epoch);
                Assert(secondBlock.Start == 65_537 && secondBlock.End == 131_072);
                // T-0264: a world whose blocks were issued at the old 2^20 size continues after its highest block.
                Assert(GatewayRegistry.NextNetworkIdBlockStart([new NetworkIdBlock(1, 1_048_576, "n", 1, DateTimeOffset.UnixEpoch), new NetworkIdBlock(1_048_577, 2_097_152, "n", 2, DateTimeOffset.UnixEpoch)]) == 2_097_153);
                Assert(GatewayRegistry.NextNetworkIdBlockStart([]) == 1);
                AssertConflict(() => registry.AllocateNetworkIdBlock(node.Node, node.Epoch));
                Assert(registry.GetPartitionOwner(7) == owner);
                Assert(registry.GetNodeByEndpoint("127.0.0.1:27018/") == target);
                PartitionOwnership reclaimed = registry.ClaimPartition(7, restarted.Node, restarted.Epoch, owner.Generation);
                HandoverRecord handover = registry.Begin("h1", 7, restarted.Node, restarted.Epoch, reclaimed.Generation, target.Node, target.Epoch);
                Assert(ReferenceEquals(handover, registry.Begin("h1", 7, restarted.Node, restarted.Epoch, reclaimed.Generation, target.Node, target.Epoch)));
                TransferGrant handoverGrant = registry.IssueGrant(new("handover", "h1", 7, reclaimed.Generation, 0,
                    restarted.Node, restarted.Epoch, target.Node));
                registry.ValidateGrant(new(handoverGrant.Token, "handover", "h1", 7, reclaimed.Generation, 0, target.Node, target.Epoch));
                AssertConflict(() => registry.ValidateGrant(new(handoverGrant.Token, "handover", "h1", 7, reclaimed.Generation, 0, target.Node, target.Epoch)));
                AssertConflict(() => registry.Begin("other", 7, restarted.Node, restarted.Epoch, reclaimed.Generation, target.Node, target.Epoch));
                Assert(registry.Incomplete().Single().Id == "h1");
                Assert(ReferenceEquals(handover, registry.Activate("h1")));
                Assert(ReferenceEquals(handover, registry.Activate("h1")));
                Assert(registry.Commit("h1").CommittedGeneration == reclaimed.Generation + 1);
                Assert(ReferenceEquals(handover, registry.Commit("h1")));
                Assert(registry.PendingReconciliation().Single().Id == "h1");
                Assert(registry.FinalizeReconciliation("h1").Reconciled);
                AssertConflict(() => registry.Abort("h1"));

                PartitionOwnership abortOwner = registry.ClaimPartition(8, restarted.Node, restarted.Epoch, 0);
                HandoverRecord aborted = registry.Begin("h2", 8, restarted.Node, restarted.Epoch, abortOwner.Generation, target.Node, target.Epoch);
                Assert(ReferenceEquals(aborted, registry.Abort("h2")));
                Assert(ReferenceEquals(aborted, registry.Abort("h2")));
                AssertConflict(() => registry.Activate("h2"));

                PartitionOwnership lostOwner = registry.ClaimPartition(9, restarted.Node, restarted.Epoch, 0);
                long lostSourceGeneration = lostOwner.Generation;
                registry.RegisterSave(new SaveId(9, lostOwner.Generation, 1), restarted.Node, restarted.Epoch, SaveKind.HandoverTee, 1, "recoverable", 1, "/save/nine");
                registry.Begin("h3", 9, restarted.Node, restarted.Epoch, lostOwner.Generation, target.Node, target.Epoch);
                Assert(registry.AbandonReconciliation("h3") is { State: HandoverState.Aborted, Reconciled: true });
                Assert(registry.GetPartitionOwner(9) is { Node: null, Generation: var lostGeneration } && lostGeneration == lostSourceGeneration + 1);
                Assert(registry.ClaimRecoveries(target.Node, target.Epoch, [9]).Single().ResolveCover.Single().Save.SaveId.Partition == 9);
            }
            using (FileStream wal = new(Path.Combine(directory, "registry.wal"), FileMode.Append))
                wal.Write([1, 2, 3]);
            using (var recovered = new GatewayRegistry(directory, Admission))
            {
                Assert(recovered.NewestSave(7)?.Location == "/save/one");
                Assert(recovered.Get("h1")?.State == HandoverState.Committed);
                Assert(recovered.Get("h1")?.CommittedGeneration == 3);
                Assert(recovered.Get("h1")?.Reconciled == true);
                Assert(recovered.Get("h2")?.State == HandoverState.Aborted);
                Assert(recovered.Incomplete().Length == 0);
                recovered.Checkpoint();
            }
            using (var checkpointed = new GatewayRegistry(directory, Admission))
            {
                Assert(checkpointed.Cover([7]).Single().Id == "s1");
                Assert(checkpointed.NetworkIdBlocks().Select(x => x.Start).SequenceEqual([1u, 65_537u]));
                Assert(checkpointed.EntityIdRanges().Select(x => x.Index).SequenceEqual([1u, 2u]));
                NodeRegistration reloaded = Register(checkpointed, "n2", "127.0.0.1:27018", Guid.NewGuid());
                Assert(checkpointed.AllocateEntityIdRange(reloaded.Node, reloaded.Epoch).Index == 3);
            }

            Directory.Delete(directory, true);

            // Layout migration: a snapshot written under the 24/32 split (no EntityIdRangeOffsetBits
            // field, ranges 1..3 issued, next 4) loads as ranges 256/512/768 with next 1024 and is
            // persisted that way, so a second load does not migrate again.
            string legacyDir = Path.Combine(Path.GetTempPath(), "gateway-registry-legacy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(legacyDir);
            File.WriteAllText(Path.Combine(legacyDir, "registry.json"),
                "{\"EntityIdRanges\":[{\"Index\":1,\"Node\":\"n1\",\"Epoch\":1,\"Allocated\":\"2026-08-21T00:00:00+00:00\"},"
                + "{\"Index\":2,\"Node\":\"n2\",\"Epoch\":1,\"Allocated\":\"2026-08-21T00:00:00+00:00\"},"
                + "{\"Index\":3,\"Node\":\"n1\",\"Epoch\":2,\"Allocated\":\"2026-08-21T00:00:00+00:00\"}],\"NextEntityIdRange\":4}");
            using (var legacy = new GatewayRegistry(legacyDir, Admission))
            {
                Assert(legacy.EntityIdRanges().Select(x => x.Index).SequenceEqual([256u, 512u, 768u]));
                NodeRegistration n9 = Register(legacy, "n9", "127.0.0.1:27019", Guid.NewGuid());
                EntityIdRange migratedNext = legacy.AllocateEntityIdRange(n9.Node, n9.Epoch);
                Assert(migratedNext.Index == 1024 && migratedNext.Start == (1024L << 24));
                legacy.Checkpoint();
            }
            using (var again = new GatewayRegistry(legacyDir, Admission))
            {
                Assert(again.EntityIdRanges().Select(x => x.Index).SequenceEqual([256u, 512u, 768u, 1024u]));
                NodeRegistration n9 = Register(again, "n9", "127.0.0.1:27019", Guid.NewGuid());
                Assert(again.AllocateEntityIdRange(n9.Node, n9.Epoch).Index == 1025);
            }
            Directory.Delete(legacyDir, true);
            Directory.CreateDirectory(directory);
            using (var registry = new GatewayRegistry(directory, Admission))
            {
                NodeRegistration node = Register(registry, "n1", "local", Guid.NewGuid());
                PartitionOwnership p10 = registry.ClaimPartition(10, node.Node, node.Epoch, 0);
                PartitionOwnership p20 = registry.ClaimPartition(20, node.Node, node.Epoch, 0);
                PartitionOwnership p21 = registry.ClaimPartition(21, node.Node, node.Epoch, 0);
                for (ulong id = 1; id <= 4; id++) registry.ClaimPartition(id, node.Node, node.Epoch, 0);
                ulong[] allocated = registry.AllocatePartitionIds(node.Node, node.Epoch, 2);
                Assert(allocated.SequenceEqual([5UL, 6UL]));
                PartitionOwnership lineageParent = registry.ClaimPartition(30, node.Node, node.Epoch, 0);
                PartitionVersion lineageInput = new(30, lineageParent.Generation);
                registry.Heartbeat(node.Node, node.Epoch, [lineageInput], [new(42, 30, 1, lineageInput.Generation)]);
                registry.NoteArrival(30);
                LineageRecord applied = registry.ApplyLineage("atomic-split", LineageKind.Split, node.Node, node.Epoch,
                    [lineageInput], allocated, new() { [allocated[0]] = [1], [allocated[1]] = [2] }, 5);
                // T-0251: the children of a split inherit the parent's arrival, so a split does not release the
                // balance residence of a partition that just landed (the Earth base, re-ID'd 31 times in 10 min).
                Assert(registry.ArrivedAt(allocated[0]) == registry.ArrivedAt(30) && registry.ArrivedAt(30) != null);
                Assert(registry.ArrivedAt(allocated[1]) == registry.ArrivedAt(30));
                // #1304: a merge survivor and both sides of a transfer keep their OWN arrival. octahedron-1191's lane
                // partitions absorbed a just-woken parked grid every 1-2 min and stayed residence held for hours.
                foreach (ulong id in new ulong[] { 960, 961, 962, 963 }) registry.ClaimPartition(id, node.Node, node.Epoch, 0);
                registry.NoteArrival(961);
                registry.NoteArrival(962);
                registry.ApplyLineage("absorb-arrival", LineageKind.Merge, node.Node, node.Epoch,
                    [new(960, registry.GetPartitionOwner(960).Generation), new(961, registry.GetPartitionOwner(961).Generation)], [960], null, 5);
                Assert(registry.ArrivedAt(960) == null);
                registry.ApplyLineage("transfer-arrival", LineageKind.Split, node.Node, node.Epoch,
                    [new(962, registry.GetPartitionOwner(962).Generation), new(963, registry.GetPartitionOwner(963).Generation)], [962, 963],
                    new() { [962] = [7], [963] = [8] }, 5);
                Assert(registry.ArrivedAt(963) == null && registry.ArrivedAt(962) != null);
                Assert(applied.Outputs.All(x => registry.GetPartitionOwner(x.Partition).Node == node.Node));
                Assert(!registry.Lineage().Single(x => x.OpId == "atomic-split").Collapsed);
                registry.RegisterSave(new SaveId(allocated[0], applied.Outputs[0].Generation, 1), node.Node, node.Epoch, SaveKind.Periodic, 1, "split-a", 6, "n1/split-a");
                Assert(!registry.Lineage().Single(x => x.OpId == "atomic-split").Collapsed);
                registry.RegisterSave(new SaveId(allocated[1], applied.Outputs[1].Generation, 1), node.Node, node.Epoch, SaveKind.Periodic, 1, "split-b", 6, "n1/split-b");
                Assert(registry.Lineage().Single(x => x.OpId == "atomic-split").Collapsed);
                Assert(registry.ResolveClientRoute(42)?.Partition == allocated[0]);
                Assert(registry.ApplyLineage("atomic-split", LineageKind.Split, node.Node, node.Epoch,
                    [lineageInput], allocated, new() { [allocated[0]] = [1], [allocated[1]] = [2] }, 5).Collapsed);
                AssertConflict(() => registry.ApplyLineage("atomic-split", LineageKind.Split, node.Node, node.Epoch,
                    [lineageInput], allocated.Reverse().ToArray(), new() { [allocated[0]] = [1], [allocated[1]] = [2] }, 5));

                ulong retainedChild = registry.AllocatePartitionIds(node.Node, node.Epoch, 1).Single();
                PartitionOwnership retainedParent = registry.ClaimPartition(40, node.Node, node.Epoch, 0);
                PartitionVersion retainedInput = new(40, retainedParent.Generation);
                registry.Heartbeat(node.Node, node.Epoch, [retainedInput], [new(43, 40, 2, retainedInput.Generation)]);
                LineageRecord retainedSplit = registry.ApplyLineage("retained-split", LineageKind.Split, node.Node, node.Epoch,
                    [retainedInput], [40, retainedChild], new() { [40] = [1], [retainedChild] = [2] }, 6);
                Assert(registry.ResolveClientRoute(43)?.Partition == retainedChild);
                registry.Heartbeat(node.Node, node.Epoch, retainedSplit.Outputs, [new(43, 40, 2, retainedInput.Generation)]);
                Assert(registry.ResolveClientRoute(43)?.Partition == retainedChild);
                // A placement can outlive its partition. RestoreClientRoute stores the binding and
                // only then validates it, so a retirement in between leaves behind a binding that no
                // retry can ever satisfy - the client hits the same conflict forever, and the
                // heartbeat that would repair it only runs once the client has attached. Resolution
                // must therefore drop a retired placement and fall soft to the home path, while a
                // placement on a partition that is merely unowned still conflicts (asserted in the
                // `leases` section, as BUSY since T-0197) because that one does come back.
                PartitionOwnership doomed = registry.ClaimPartition(50, node.Node, node.Epoch, 0);
                ulong survivor = registry.AllocatePartitionIds(node.Node, node.Epoch, 1).Single();
                registry.ApplyLineage("retire-50", LineageKind.Split, node.Node, node.Epoch,
                    [new(50, doomed.Generation)], [survivor], new() { [survivor] = [3] }, 7);
                AssertConflict(() => registry.RestoreClientRoute(44, new ClientPartition(50, 0)));
                Assert(registry.ResolveClientRoute(44) == null);
                // and the dead binding is gone, so it cannot strand the client a second time
                Assert(registry.GetClientPartition(44) == null);

                AssertConflict(() => registry.ApplyLineage("bad", LineageKind.Merge, node.Node, node.Epoch, [new(20, 999)], [20], null, 6));
                SaveRegistration parent = registry.RegisterSave(new SaveId(10, p10.Generation, 1), node.Node, node.Epoch, SaveKind.Final, 100, "a", 10, "n1/a");
                registry.RegisterSave(new SaveId(20, p20.Generation, 1), node.Node, node.Epoch, SaveKind.Final, 100, "b", 10, "n1/b");
                registry.RegisterSave(new SaveId(21, p21.Generation, 1), node.Node, node.Epoch, SaveKind.Final, 100, "c", 10, "n1/c");
                // Idempotent by identity, not by reference: KI-15 hands every caller a snapshot.
                SaveRegistration parentAgain = registry.RegisterSave(new SaveId(10, p10.Generation, 1), node.Node, node.Epoch, SaveKind.Final, 100, "a", 10, "n1/a");
                Assert(!ReferenceEquals(parent, parentAgain) && parentAgain.SaveId == parent.SaveId
                    && parentAgain.Locations.SetEquals(parent.Locations));
                AssertConflict(() => registry.RegisterSave(new SaveId(10, p10.Generation, 1), node.Node, node.Epoch, SaveKind.Final, 100, "wrong", 10, "n1/a"));

                registry.RecordLineage("split", LineageKind.Split, [new(10, 1)], [new(10011, 1), new(10012, 1)], new() { [10011] = [101, 102], [10012] = [103] }, 20);
                SaveCover split = registry.ResolveCover(10011).Single();
                Assert(split.Save.SaveId == parent.SaveId && split.EntityIds!.SequenceEqual([101L, 102L]));
                registry.RecordLineage("merge", LineageKind.Merge, [new(20, 1), new(21, 1)], [new(20, 1)], null, 30);
                Assert(registry.ResolveCover(20).Select(x => x.Save.SaveId.Partition).Order().SequenceEqual([20UL, 21UL]));
                // RecordLineage retires its inputs exactly like ApplyLineage, so it must repair
                // client placements the same way. The rotator's retire-empty merge goes through
                // here, and before the repair it left placements naming the retired input - the
                // client then conflicted on every route resolution for the rest of the run
                // (world 10: six pilots; world 12: the whole fleet via dissolutions).
                PartitionOwnership retiree = registry.ClaimPartition(60, node.Node, node.Epoch, 0);
                PartitionOwnership retireAnchor = registry.ClaimPartition(61, node.Node, node.Epoch, 0);
                registry.RouteClient(45, 60);
                registry.RecordLineage("retire-empty-60", LineageKind.Merge,
                    [new(60, retiree.Generation)], [new(61, retireAnchor.Generation)], null, 40);
                Assert(registry.GetClientPartition(45)?.Partition == 61);

                for (int iteration = 0; iteration < 16; iteration++)
                {
                    ulong parentId = (ulong)(100 + iteration);
                    PartitionOwnership fuzzParent = registry.ClaimPartition(parentId, node.Node, node.Epoch, 0);
                    registry.RegisterSave(new SaveId(parentId, fuzzParent.Generation, 1), node.Node, node.Epoch, SaveKind.Periodic,
                        1, "fuzz-parent-" + iteration, 1000 + iteration * 10, $"n1/fuzz-parent-{iteration}");
                    ulong[] children = registry.AllocatePartitionIds(node.Node, node.Epoch, 2);
                    long leftEntity = iteration * 2L + 1, rightEntity = leftEntity + 1;
                    LineageRecord splitFuzz = registry.ApplyLineage("fuzz-split-" + iteration, LineageKind.Split, node.Node, node.Epoch,
                        [new(parentId, fuzzParent.Generation)], children, new() { [children[0]] = [leftEntity], [children[1]] = [rightEntity] }, 1001 + iteration * 10);
                    Assert(registry.ResolveCover(children[0]).Single().EntityIds!.SequenceEqual([leftEntity]));
                    registry.RegisterSave(new SaveId(children[0], splitFuzz.Outputs[0].Generation, 1), node.Node, node.Epoch, SaveKind.Periodic,
                        1, "fuzz-left-" + iteration, 1002 + iteration * 10, $"n1/fuzz-left-{iteration}");
                    registry.RegisterSave(new SaveId(children[1], splitFuzz.Outputs[1].Generation, 1), node.Node, node.Epoch, SaveKind.Periodic,
                        1, "fuzz-right-" + iteration, 1002 + iteration * 10, $"n1/fuzz-right-{iteration}");
                    Assert(registry.Lineage().Single(x => x.OpId == "fuzz-split-" + iteration).Collapsed);
                    LineageRecord mergeFuzz = registry.ApplyLineage("fuzz-merge-" + iteration, LineageKind.Merge, node.Node, node.Epoch,
                        splitFuzz.Outputs, [children[0]], null, 1003 + iteration * 10);
                    Assert(registry.ResolveCover(children[0]).Select(x => x.Save.SaveId.Partition).Distinct().Count() == 2);
                    registry.RegisterSave(new SaveId(children[0], mergeFuzz.Outputs[0].Generation, 1), node.Node, node.Epoch, SaveKind.Final,
                        1, "fuzz-merged-" + iteration, 1004 + iteration * 10, $"n1/fuzz-merged-{iteration}");
                    Assert(registry.ResolveCover(children[0]).Single().Save.SaveId.Generation == mergeFuzz.Outputs[0].Generation);
                    Assert(registry.Lineage().Single(x => x.OpId == "fuzz-merge-" + iteration).Collapsed);
                }
				PartitionOwnership chainedParent = registry.ClaimPartition(500, node.Node, node.Epoch, 0);
				registry.RegisterSave(new SaveId(500, chainedParent.Generation, 1), node.Node, node.Epoch, SaveKind.Periodic, 1, "chain-parent", 2000, "n1/chain-parent");
				ulong[] chainedChildren = registry.AllocatePartitionIds(node.Node, node.Epoch, 2);
				LineageRecord chainedSplit = registry.ApplyLineage("chain-split", LineageKind.Split, node.Node, node.Epoch,
					[new(500, chainedParent.Generation)], chainedChildren, new() { [chainedChildren[0]] = [1], [chainedChildren[1]] = [2] }, 2001);
				LineageRecord chainedMerge = registry.ApplyLineage("chain-merge", LineageKind.Merge, node.Node, node.Epoch,
					chainedSplit.Outputs, [chainedChildren[0]], null, 2002);
				registry.RegisterSave(new SaveId(chainedChildren[0], chainedMerge.Outputs[0].Generation, 1), node.Node, node.Epoch,
					SaveKind.Final, 1, "chain-final", 2003, "n1/chain-final");
				Assert(registry.Lineage().Where(x => x.OpId is "chain-split" or "chain-merge").All(x => x.Collapsed));

                // The recovery auto-pick stopped sorting the whole candidate set to take one element
                // on 2026-08-22. The ordering it now applies pairwise must be the ordering the
                // OrderByDescending(client).ThenByDescending(home).ThenByDescending(catalogued)
                // .ThenBy(id) chain produced - checked here over randomized candidate sets, because
                // the first attempt at writing it as one expression hit C# precedence and silently
                // skipped the first candidate whenever it had no client.
                {
                    var ranks = new Random(20260822);
                    for (int trial = 0; trial < 2000; trial++)
                    {
                        var candidates = Enumerable.Range(0, 1 + ranks.Next(8)).Select(index =>
                            (Client: ranks.Next(2) == 0, Home: ranks.Next(2) == 0,
                             Catalogued: (long)ranks.Next(3), Id: (ulong)ranks.Next(1, 6))).ToArray();
                        var expected = candidates
                            .OrderByDescending(x => x.Client).ThenByDescending(x => x.Home)
                            .ThenByDescending(x => x.Catalogued).ThenBy(x => x.Id).First();
                        (bool Client, bool Home, long Catalogued, ulong Id)? best = null;
                        foreach (var candidate in candidates)
                            if (best == null || GatewayRegistry.OutranksForRecovery(
                                    (candidate.Client, candidate.Home, candidate.Catalogued), candidate.Id,
                                    (best.Value.Client, best.Value.Home, best.Value.Catalogued), best.Value.Id))
                                best = candidate;
                        Assert(best!.Value.Client == expected.Client && best.Value.Home == expected.Home
                            && best.Value.Catalogued == expected.Catalogued && best.Value.Id == expected.Id);
                    }
                    Console.WriteLine("recovery candidate ordering matches the previous sort over 2000 randomized sets");
                }

                // UpdateCollapsed was rewritten on 2026-08-22 from a do/while that rescanned the whole
                // lineage list (O(lineage^3) to converge, under the registry lock, on every save
                // registration) into a single pass plus a worklist. The closure is monotone, so the
                // result must be identical - checked here against a brute-force fixpoint computed the
                // old way over the registry's own live lineage and save table, rather than trusted.
                {
                    LineageRecord[] records = registry.Lineage();
                    SaveRegistration[] saves = registry.Saves();
                    bool[] reference = new bool[records.Length];
                    bool changed;
                    do
                    {
                        changed = false;
                        for (int i = 0; i < records.Length; i++)
                        {
                            if (reference[i]) continue;
                            if (!records[i].Outputs.All(output =>
                                    saves.Any(save => save.SaveId.Partition == output.Partition
                                        && save.SaveId.Generation >= output.Generation && !save.PendingDelete && save.Locations.Count != 0)
                                    || records.Where((_, j) => reference[j]).Any(next => next.Inputs.Contains(output))))
                                continue;
                            reference[i] = true;
                            changed = true;
                        }
                    } while (changed);
                    for (int i = 0; i < records.Length; i++)
                        Assert(records[i].Collapsed == reference[i]);
                    Console.WriteLine($"lineage collapse closure matches the brute-force fixpoint over {records.Length} records");
                }

				// SAV-07 shape: a partition with no save merges with a split child, then splits out
				// again. The member that came from the saveless side must yield NO cover - not a
				// vacuous EntityIds:[] cover from the sibling branch - while the member that came
				// through the saved branch still resolves through the merge.
				PartitionOwnership savelessInput = registry.ClaimPartition(600, node.Node, node.Epoch, 0);
				PartitionOwnership savedInput = registry.ClaimPartition(601, node.Node, node.Epoch, 0);
				long sav07Generation = savedInput.Generation;
				registry.RegisterSave(new SaveId(601, sav07Generation, 1), node.Node, node.Epoch, SaveKind.Periodic, 1, "sav07-parent", 3000, "n1/sav07-parent");
				ulong[] sav07First = registry.AllocatePartitionIds(node.Node, node.Epoch, 2);
				LineageRecord sav07Split = registry.ApplyLineage("sav07-split", LineageKind.Split, node.Node, node.Epoch,
					[new(601, sav07Generation)], sav07First, new() { [sav07First[0]] = [11], [sav07First[1]] = [12] }, 3001);
				LineageRecord sav07Merge = registry.ApplyLineage("sav07-merge", LineageKind.Merge, node.Node, node.Epoch,
					[new(600, savelessInput.Generation), sav07Split.Outputs[1]], [600], null, 3002);
				ulong[] sav07Second = registry.AllocatePartitionIds(node.Node, node.Epoch, 2);
				registry.ApplyLineage("sav07-resplit", LineageKind.Split, node.Node, node.Epoch,
					[new(600, sav07Merge.Outputs[0].Generation)], sav07Second, new() { [sav07Second[0]] = [13], [sav07Second[1]] = [12] }, 3003);
				Assert(registry.ResolveCover(sav07Second[0]).Length == 0);
				SaveCover sav07Cover = registry.ResolveCover(sav07Second[1]).Single();
				Assert(sav07Cover.Save.SaveId == new SaveId(601, sav07Generation, 1) && sav07Cover.EntityIds!.SequenceEqual([12L]));

                registry.RegisterSave(new SaveId(10, p10.Generation, 2), node.Node, node.Epoch, SaveKind.Periodic, 90, "d", 40, "n1/d");
                registry.RegisterSave(new SaveId(10, p10.Generation, 3), node.Node, node.Epoch, SaveKind.Periodic, 80, "e", 50, "n1/e");
                registry.CreateSnapshot("snap", SnapshotKind.Online, 45, [parent.SaveId], []);
                registry.ClaimPartition(9999, node.Node, node.Epoch, 0);
                SnapshotRegistration online = registry.CreateOnlineSnapshot("online", 44);
                Assert(online.PartitionRefs.Length > 0 && online.Dirty.Contains("partition:9999"));
                DeleteDirective[] deletes = registry.PlanRetention(1, 1);
                DeleteDirective middle = deletes.Single(x => x.SaveId == new SaveId(10, 1, 2));
                Assert(registry.NewestSave(10)?.SaveId.Sequence == 3);
                Assert(registry.PendingDeletes(node.Node, node.Epoch).Any(x => x.SaveId == middle.SaveId));
                Assert(!registry.AcknowledgeDelete(middle.SaveId, "n1/not-the-file"));
                Assert(registry.AcknowledgeDelete(middle.SaveId, middle.Location));
                registry.Checkpoint();
            }
            using (var recovered = new GatewayRegistry(directory, Admission))
            {
                Assert(recovered.ResolveCover(10011).Single().EntityIds!.SequenceEqual([101L, 102L]));
                Assert(recovered.NewestSave(10)?.SaveId.Sequence == 3);
                Assert(recovered.Partitions().Count(x => x.Node != null && x.Partition != 10 && x.Partition != 20 && x.Partition != 21) >= 2);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// T-0227 store durability. A crash is a copy of the registry directory (database + its WAL)
    /// taken with the registry still open: after EVERY committed flush the copy reopens to exactly
    /// the state that flush wrote; a torn final commit is dropped whole; a corrupt database refuses
    /// to open; a legacy snapshot + WAL (CGR2) is migrated once and its files are set aside.
    /// </summary>
    private static void TestStoreRecovery()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-store-self-test-" + Guid.NewGuid());
        static string CopyStore(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from, RegistryStore.FileName + "*"))
                if (!file.EndsWith("-shm", StringComparison.Ordinal))
                    File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            return to;
        }
        try
        {
            string clean = Path.Combine(directory, "clean");
            List<string> nodes = ["node-0"];
            List<ulong> partitions = [];
            List<uint> blocks = [];
            var random = new Random(0x47575902);
            string lastCrash, previousCrash = "";
            (string[] Nodes, ulong[] Partitions, uint[] Blocks) last, previous = ([], [], []);
            using (var registry = new GatewayRegistry(clean, Admission, () => DateTimeOffset.UnixEpoch))
            {
                NodeRegistration owner = Register(registry, nodes[0], "endpoint-0", Guid.Parse("00000000-0000-0000-0000-000000000001"));
                registry.Flush();
                last = ([.. nodes], [], []);
                lastCrash = CopyStore(clean, Path.Combine(directory, "crash-0"));
                for (int i = 1; i <= 48; i++)
                {
                    switch (random.Next(3))
                    {
                        case 0:
                            string node = "node-" + i;
                            Register(registry, node, "endpoint-" + i, Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}"));
                            nodes.Add(node);
                            break;
                        case 1:
                            ulong partition = (ulong)i;
                            registry.ClaimPartition(partition, owner.Node, owner.Epoch, 0);
                            partitions.Add(partition);
                            break;
                        default:
                            NetworkIdBlock block = registry.AllocateNetworkIdBlock(owner.Node, owner.Epoch);
                            blocks.Add(block.Start);
                            break;
                    }
                    registry.Flush();
                    (previous, previousCrash) = (last, lastCrash);
                    last = (nodes.Order().ToArray(), partitions.Order().ToArray(), [.. blocks]);
                    lastCrash = CopyStore(clean, Path.Combine(directory, "crash-" + i));
                    using var recovered = new GatewayRegistry(CopyStore(lastCrash, Path.Combine(directory, "open-" + i)), Admission);
                    AssertRegistryShadow(recovered, last);
                }

                // Torn final commit: the last flush's WAL frames cut short. SQLite drops the incomplete
                // transaction whole, so the state is exactly the previous commit's.
                string torn = CopyStore(lastCrash, Path.Combine(directory, "torn"));
                string tornWal = Path.Combine(torn, RegistryStore.FileName + "-wal");
                string previousWal = Path.Combine(previousCrash, RegistryStore.FileName + "-wal");
                Assert(File.Exists(tornWal) && File.Exists(previousWal));
                long cut = new FileInfo(previousWal).Length;
                Assert(new FileInfo(tornWal).Length > cut + 1);      // the last commit really is in this WAL
                using (FileStream file = new(tornWal, FileMode.Open)) file.SetLength(new FileInfo(tornWal).Length - 1);
                using (var recovered = new GatewayRegistry(torn, Admission))
                    AssertRegistryShadow(recovered, previous);
            }

            // A corrupt database refuses to open instead of starting from nothing.
            string corrupt = CopyStore(lastCrash, Path.Combine(directory, "corrupt"));
            using (var checkpointed = new GatewayRegistry(corrupt, Admission)) { }   // folds the WAL into the file
            Assert(new FileInfo(Path.Combine(corrupt, RegistryStore.FileName)).Length > 4096);
            using (FileStream file = new(Path.Combine(corrupt, RegistryStore.FileName), FileMode.Open))
            {
                file.Position = 0;
                file.Write("garbage-garbage-"u8);
            }
            AssertThrows(() => { using var _ = new GatewayRegistry(corrupt, Admission); });

            // Migration: a legacy CGR2 WAL record (compressed whole state) with no snapshot.
            string legacy = Path.Combine(directory, "legacy");
            Directory.CreateDirectory(legacy);
            byte[] json = System.Text.Encoding.UTF8.GetBytes(
                "{\"StateSequence\":5,\"NextPartitionId\":8,\"Partitions\":{\"7\":{\"Partition\":7,\"Node\":null,\"Epoch\":0,\"Generation\":1}},"
                + "\"AllocatedPartitionIds\":[7]}");
            byte[] payload;
            using (var buffer = new MemoryStream())
            {
                using (var deflate = new System.IO.Compression.DeflateStream(buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                    deflate.Write(json);
                payload = buffer.ToArray();
            }
            byte[] record = new byte[12 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(record, 0x32524743);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), GatewayRegistry.LegacyCrc32(payload));
            payload.CopyTo(record, 12);
            File.WriteAllBytes(Path.Combine(legacy, "registry.wal"), record);
            using (var migrated = new GatewayRegistry(legacy, Admission))
                Assert(migrated.Partitions().Select(x => x.Partition).SequenceEqual([7ul]));
            Assert(!File.Exists(Path.Combine(legacy, "registry.wal")));
            Assert(Directory.GetFiles(legacy, "registry.wal.migrated-*").Length == 1);
            using (var again = new GatewayRegistry(legacy, Admission))
                Assert(again.Partitions().Select(x => x.Partition).SequenceEqual([7ul]));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// T-0227 store cost. An idle cluster's 1 Hz lease renewals must not commit (that is how an empty
    /// world once wrote a 32 GB WAL), a flush carries only the rows that changed - never the whole
    /// state - and the volatile timestamps deliberately left unpersisted are re-granted one TTL at
    /// boot instead of fencing nodes that are still alive.
    /// </summary>
    private static void TestStoreGrowth()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cluster-gateway-store-growth-self-test-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        try
        {
            string idle = Path.Combine(directory, "idle");
            long generation;
            using (var registry = new GatewayRegistry(idle, Admission, () => now, TimeSpan.FromSeconds(10)))
            {
                NodeRegistration node = Register(registry, "first", "one", Guid.NewGuid());
                generation = registry.ClaimPartition(1, node.Node, node.Epoch, 0).Generation;
                for (ulong partition = 2; partition <= 400; partition++)
                    registry.ClaimPartition(partition, node.Node, node.Epoch, 0);
                registry.Flush();
                // The first weight-bearing heartbeat is a material change and commits once...
                long commits = registry.StoreCommits;
                registry.Heartbeat(node.Node, node.Epoch, [new(1, generation, 5)]);
                registry.Flush();
                Assert(registry.StoreCommits == commits + 1);
                commits = registry.StoreCommits;
                // ...and an idle hour of 1 Hz lease renewals with an advancing simulation frame commits nothing.
                for (int i = 1; i <= 3600; i++)
                {
                    now += TimeSpan.FromSeconds(1);
                    registry.Heartbeat(node.Node, node.Epoch, [new(1, generation, 5)], simulationFrame: 60 * i);
                }
                registry.Flush();
                Assert(registry.StoreCommits == commits);
                // A changed member count is durable state again - and the commit carries a handful of
                // rows (the weight row, the renewed partition row, changed small-state rows), not 400.
                long rows = registry.StoreRowsWritten;
                registry.Heartbeat(node.Node, node.Epoch, [new(1, generation, 7)]);
                registry.Flush();
                Assert(registry.StoreCommits == commits + 1);
                Assert(registry.StoreRowsWritten - rows is > 0 and <= 12);
            }
            // The idle hour committed no lease renewals, so the recovered timestamps are stale by
            // design: the fencing sweep holds for one TTL from boot so a live node gets to renew
            // before it can be declared dead...
            using (var restarted = new GatewayRegistry(idle, Admission, () => now, TimeSpan.FromSeconds(10)))
            {
                Assert(restarted.ExpireLeases() == 0);
                Assert(restarted.Nodes().Single() is { Node: "first", State: NodeState.Active });
                Assert(restarted.GetPartitionOwner(1).Node == "first");
                // ...while a node still silent when the hold lifts expires as before.
                now += TimeSpan.FromSeconds(11);
                Assert(restarted.ExpireLeases() == 400);
                Assert(restarted.Nodes().Single().State == NodeState.Dead);
            }

            // The live audit: a change that bypassed tracking (a missing Touch) is found by the next
            // write's audit and is in the store after the write after that. The full verifier would
            // (rightly) flag the untracked change itself, so it is off for this part.
            string audit = Path.Combine(directory, "audit");
            bool verify = GatewayRegistry.VerifyStoreAfterWrite;
            GatewayRegistry.VerifyStoreAfterWrite = false;
            try
            {
                using var registry = new GatewayRegistry(audit, Admission, () => now, TimeSpan.FromSeconds(10));
                NodeRegistration node = Register(registry, "audited", "one", Guid.NewGuid());
                long claimed = registry.ClaimPartition(5, node.Node, node.Epoch, 0).Generation;
                registry.Flush();
                registry.TestUntrackedChange(5, 100);
                registry.Flush();
                Assert(registry.StoreAuditRepairs == 1);
                registry.TestPersist();
                registry.Flush();
                string copy = Path.Combine(directory, "audit-copy");
                Directory.CreateDirectory(copy);
                foreach (string file in Directory.GetFiles(audit, RegistryStore.FileName + "*"))
                    if (!file.EndsWith("-shm", StringComparison.Ordinal)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
                using var reopened = new GatewayRegistry(copy, Admission, () => now, TimeSpan.FromSeconds(10));
                Assert(reopened.GetPartitionOwner(5).Generation == claimed + 100);
            }
            finally { GatewayRegistry.VerifyStoreAfterWrite = verify; }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void AssertRegistryShadow(GatewayRegistry registry, (string[] Nodes, ulong[] Partitions, uint[] Blocks) shadow)
    {
        Assert(registry.Nodes().Select(x => x.Node).Order().SequenceEqual(shadow.Nodes));
        Assert(registry.Partitions().Select(x => x.Partition).Order().SequenceEqual(shadow.Partitions));
        Assert(registry.NetworkIdBlocks().Select(x => x.Start).SequenceEqual(shadow.Blocks));
    }

    private static void AssertConflict(Action action)
    {
        try { action(); }
        catch (RegistryConflictException) { return; }
        throw new InvalidOperationException("self-test failed: expected registry conflict");
    }

    private static void AssertBusy(Action action)
    {
        try { action(); }
        catch (RegistryBusyException) { return; }
        throw new InvalidOperationException("self-test failed: expected registry busy (retryable) answer");
    }

    internal sealed record CostEncounterSide(ulong Id, double X, int Members, int Pcu, long Bytes, ulong[] Clients);
    internal sealed record CostEncounter(string Name, CostEncounterSide[] Source, CostEncounterSide[] Target,
        ulong ExpectMoving, bool ExpectToTarget);

    /// <summary>
    /// Replays recorded encounters through the §7.5 chooser. With no replay file this runs the canonical
    /// bootstrap encounters; the P3-CST-01 calibration writes its blessed encounter JSON and points
    /// CLUSTER_COST_REPLAY at it, which makes this section the drift gate for the fitted model.
    /// </summary>
    private static void TestCostModel()
    {
        CostModel model = CostModel.Load(Environment.GetEnvironmentVariable("CLUSTER_COST_MODEL"));
        string? replay = Environment.GetEnvironmentVariable("CLUSTER_COST_REPLAY");
        CostEncounter[] encounters = replay == null
            ? DefaultCostEncounters
            : System.Text.Json.JsonSerializer.Deserialize<CostEncounter[]>(File.ReadAllText(replay))
                ?? throw new InvalidOperationException("Empty cost replay file " + replay);
        foreach (CostEncounter encounter in encounters)
        {
            static PartitionReport Side(CostEncounterSide[] partitions) => new(3000, 1000,
                partitions.Select(p => new PartitionInfo(p.Id, p.Members, true,
                    new PartitionSphere(p.X, 0, 0, 100), 0, p.Clients, p.Pcu, p.Bytes)).ToArray(), 0, 50);
            Move? move = ChooseMove(Side(encounter.Source), Side(encounter.Target), 4000,
                new HashSet<ulong>(), _ => false, model);
            if (move is not { } chosen || chosen.Moving.Id != encounter.ExpectMoving
                || chosen.ToTarget != encounter.ExpectToTarget)
                throw new InvalidOperationException($"cost-model replay '{encounter.Name}': expected "
                    + $"{encounter.ExpectMoving} toTarget={encounter.ExpectToTarget}, got "
                    + (move is { } m ? $"{m.Moving.Id} toTarget={m.ToTarget}" : "no move"));
        }
        Console.WriteLine($"cost-model replay: {encounters.Length} encounters agree with the chooser");
    }

    private static readonly CostEncounter[] DefaultCostEncounters =
    [
        new("character with client travels to the base",
            [new(1, 0, 1, 0, 10_000, [77])], [new(2, 2000, 8, 5000, 2_066_000, [])], 1, true),
        new("same encounter from the target side back",
            [new(2, 0, 8, 5000, 2_066_000, [])], [new(1, 2000, 1, 0, 10_000, [77])], 1, false),
        new("clientless crumb moves before a client switch",
            [new(1, 0, 1, 10, 14_000, [])], [new(2, 2000, 1, 0, 10_000, [77])], 1, true),
    ];

    /// <summary>
    /// The placement decisions, which are pure. The property that matters most is that neither pass
    /// oscillates: a live cluster spent an evening at 0.4 simulation speed because co-location kept trading
    /// one violation for another, and a balancer is the easiest place to reintroduce exactly that.
    /// </summary>
    private static void TestPartitionPlacement()
    {
        const double envelope = 4000;
        // Fixtures estimate snapshot bytes exactly like the node does (PartitionManager
        // .EstimatedSnapshotBytes), so the §7.5 cost the choosers see here is the one they see live.
        static PartitionInfo At(ulong id, double x, double radius = 100, int members = 1, int pcu = 0,
            ulong[]? clients = null) =>
            new(id, members, true, new PartitionSphere(x, 0, 0, radius), 0, clients ?? [], pcu,
                400L * pcu + 8_000L * members + 2_000);
        static PartitionReport Report(PartitionInfo[] partitions, double threadLoad = 50) =>
            new(3000, 1000, partitions, 0, threadLoad);
        static bool Never(ulong _) => false;
        static bool Always(ulong _) => true;
        HashSet<ulong> none = new();

        // Nothing to do when both sides are already co-located, however close they are.
        Assert(ChooseMove(Report([At(1, 0), At(2, 500)]), Report([At(3, 100000)]), envelope, none, Never) is null);

        // The case that thrashed: A and B on the source, C on the target, B within envelope of both. B is
        // the closest pair either way, but the component is {A,B,C} and the source holds the majority, so
        // the fix is to move C - once - not to shuttle B forever.
        PartitionReport twoOnSource = Report([At(1, 0, 100, 10), At(2, 3000, 100, 10)]);
        PartitionReport oneOnTarget = Report([At(3, 6000, 100, 1)]);
        Move first = ChooseMove(twoOnSource, oneOnTarget, envelope, none, Never)!.Value;
        Assert(first.Moving.Id == 3 && !first.ToTarget);
        // Apply it and the decision must be "done", not the reverse move.
        Assert(ChooseMove(Report([At(1, 0, 100, 10), At(2, 3000, 100, 10), At(3, 6000, 100, 1)]),
            Report([]), envelope, none, Never) is null);

        // A client-bearing side no longer pins its node outright - the symmetric switch removed that
        // rule - but the γ term prices the switch, so against an otherwise-equal side the client-bearing
        // one is still the home and the clientless one moves.
        Move pinned = ChooseMove(Report([At(1, 0)]), Report([At(2, 2000, 100, 1, 0, [77])]), envelope, none, Never)!.Value;
        Assert(pinned.Moving.Id == 1 && pinned.ToTarget);

        // T-0141 (2026-09-15): two lattice clusters price within a few percent of each other; that is
        // a NEAR-TIE and the lighter node is the home. Same shape, loads swapped, the other side moves;
        // without load knowledge (the default 0/0) the exact price decides as before; and a real price
        // difference (a lone ship against a base) is never overridden by load.
        PartitionReport clusterA = Report([At(1, 0, 1000, 31, 38000)]);
        PartitionReport clusterB = Report([At(2, 5000, 1000, 32, 39000)]);
        Move tieToLight = ChooseMove(clusterA, clusterB, envelope, none, Never, new CostModel(), null, null, null,
            loadSource: 1_300_000, loadTarget: 400_000)!.Value;
        Assert(tieToLight.Moving.Id == 1 && tieToLight.ToTarget && tieToLight.Why.Contains("near-tie"));
        Move tieToLightReverse = ChooseMove(clusterA, clusterB, envelope, none, Never, new CostModel(), null, null, null,
            loadSource: 400_000, loadTarget: 1_300_000)!.Value;
        Assert(tieToLightReverse.Moving.Id == 2 && !tieToLightReverse.ToTarget);
        Move tieNoLoad = ChooseMove(clusterA, clusterB, envelope, none, Never)!.Value;
        Assert(tieNoLoad.Moving.Id == 1 && tieNoLoad.ToTarget && !tieNoLoad.Why.Contains("near-tie"));   // B prices higher
        Move tieOff = ChooseMove(clusterA, clusterB, envelope, none, Never, new CostModel { HomeTieFraction = 0 }, null, null, null,
            loadSource: 400_000, loadTarget: 1_300_000)!.Value;
        Assert(tieOff.Moving.Id == 1 && tieOff.ToTarget);
        Move shipToBase = ChooseMove(Report([At(3, 0, 100, 1, 1200) with { MaxMemberSpeed = 100 }]), clusterB, envelope, none, Never,
            new CostModel(), null, null, null, loadSource: 400_000, loadTarget: 1_300_000)!.Value;
        Assert(shipToBase.Moving.Id == 3 && shipToBase.ToTarget && !shipToBase.Why.Contains("near-tie"));

        // §7.5 direction, both ways: a lone character (no PCU, one member, its client attached) prices
        // far below a real base, so the character travels to the content - INCLUDING from the target
        // side back to the source, the direction the one-way switch could never serve.
        Move toBase = ChooseMove(Report([At(1, 0, 100, 1, 0, [77])]), Report([At(2, 2000, 100, 8, 5000)]),
            envelope, none, Never)!.Value;
        Assert(toBase.Moving.Id == 1 && toBase.ToTarget);
        Move fromTarget = ChooseMove(Report([At(2, 0, 100, 8, 5000)]), Report([At(1, 2000, 100, 1, 0, [77])]),
            envelope, none, Never)!.Value;
        Assert(fromTarget.Moving.Id == 1 && !fromTarget.ToTarget);

        // Partitioning.md §7.4: a teleport intent forces the character side to the destination's owner
        // even though the crumb there is the cheaper §7.5 mover; without the intent the same encounter
        // is priced normally and the crumb travels to the player. Both directions, so the trigger rule
        // and the price rule are each pinned by a case the other would fail.
        Move directed = ChooseMove(Report([At(10, 0, 100, 1, 0, [77])]), Report([At(11, 2000, 100, 1, 10)]),
            envelope, none, Never, null, [77])!.Value;
        Assert(directed.Moving.Id == 10 && directed.ToTarget);
        Move priced = ChooseMove(Report([At(10, 0, 100, 1, 0, [77])]), Report([At(11, 2000, 100, 1, 10)]),
            envelope, none, Never)!.Value;
        Assert(priced.Moving.Id == 11 && !priced.ToTarget);

        // Identical sides settle on the target - the §7.1 tie-break chain ends in a constant, so the
        // choice never depends on report order and the passes cannot oscillate.
        Move settled = ChooseMove(Report([At(1, 0, 100, 3, 700)]), Report([At(2, 2000, 100, 3, 700)]),
            envelope, none, Never)!.Value;
        Assert(settled.Moving.Id == 1 && settled.ToTarget);

        // ===== T-0141 second attempt: congestion PRICED into §7.5, not vetoed on top of it =====
        // The first attempt (gateway da56622) refused these co-locations outright and was rolled
        // back after 56 minutes live: refusing leaves the pair straddling two nodes inside
        // SyncDistance, which IS the 3000m sphere-gap invariant, and the gateway accelerates
        // co-location against that violation - so the guard manufactured the condition that made
        // the system fight it (0.86 -> 2.64 invariant alarms/min, 3.1x). Pricing it inside the
        // comparison can only change the DIRECTION, never whether the move happens.
        CostModel congested = new CostModel(LoadHomeWeight: 0.5);

        // THE PROPERTY THE FIRST ATTEMPT BROKE, asserted directly and at both extremes of the
        // weight: a component spanning two nodes ALWAYS yields a move. No load figure, however
        // lopsided, may produce the null that means "co-located, nothing to do".
        foreach (double weight in new[] { 0.0, 0.5, 1.0, 50.0 })
            foreach ((double ls, double lt) in new[] { (1.0, 1.0), (1.0, 1e9), (1e9, 1.0), (0.0, 5000.0) })
                Assert(ChooseMove(Report([At(1, 0, 100, 10, 9000)]), Report([At(2, 2000, 100, 2, 1000)]),
                    envelope, none, Never, new CostModel(LoadHomeWeight: weight), null, null, null, ls, lt)
                    is not null);

        // Weight 0 is EXACTLY §7.5 - the fixture below flips under load but must not move without
        // the weight, whatever the loads say. This is the off-switch, asserted rather than assumed.
        Move unweighted = ChooseMove(Report([At(1, 0, 100, 10, 1200)]), Report([At(2, 2000, 100, 2, 1000)]),
            envelope, none, Never, new CostModel(), null, null, null, 100, 100000)!.Value;
        Assert(unweighted.Moving.Id == 2 && !unweighted.ToTarget);

        // Balanced nodes: the congestion term is zero and §7.5 decides alone, so the bigger side
        // stays and the smaller one travels to it - the same answer as the control above.
        Move balanced = ChooseMove(Report([At(1, 0, 100, 10, 1200)]), Report([At(2, 2000, 100, 2, 1000)]),
            envelope, none, Never, congested, null, null, null, 50000, 50000)!.Value;
        Assert(balanced.Moving.Id == 2 && !balanced.ToTarget);

        // Now overload the side §7.5 would have picked as home. The move still happens; it reverses.
        // Source is the bigger side (1200 PCU vs 1000) so §7.5 alone homes it there; with the source
        // carrying 4x the load, the content goes to the quiet node instead.
        Move reversed = ChooseMove(Report([At(1, 0, 100, 10, 1200)]), Report([At(2, 2000, 100, 2, 1000)]),
            envelope, none, Never, congested, null, null, null, 200000, 50000)!.Value;
        Assert(reversed.Moving.Id == 1 && reversed.ToTarget);

        // The term is a PRICE, so a big enough cost gap outvotes a mild imbalance. Same 4x load on
        // the source, but now the source side is expensive enough to move that keeping it put still
        // wins - the congestion term bends §7.5, it does not override it.
        Move outvoted = ChooseMove(Report([At(1, 0, 100, 40, 60000)]), Report([At(2, 2000, 100, 2, 1000)]),
            envelope, none, Never, congested, null, null, null, 200000, 50000)!.Value;
        Assert(outvoted.Moving.Id == 2 && !outvoted.ToTarget);

        // A missing or zero load reading disables the term rather than reading as "idle, send it
        // here". An unreported node must never become attractive by virtue of saying nothing.
        Move unreported = ChooseMove(Report([At(1, 0, 100, 10, 1200)]), Report([At(2, 2000, 100, 2, 1000)]),
            envelope, none, Never, congested, null, null, null, 200000, 0)!.Value;
        Assert(unreported.Moving.Id == 2 && !unreported.ToTarget);

        // Symmetry: the same imbalance from the other side gives the mirror answer, so the rule
        // cannot depend on which report the caller passed first.
        Move mirrored = ChooseMove(Report([At(1, 0, 100, 2, 1000)]), Report([At(2, 2000, 100, 10, 1200)]),
            envelope, none, Never, congested, null, null, null, 50000, 200000)!.Value;
        Assert(mirrored.Moving.Id == 2 && !mirrored.ToTarget);

        // Backoff takes a partition out of consideration entirely rather than stalling the pass.
        Assert(ChooseMove(Report([At(1, 0)]), Report([At(2, 2000)]), envelope, none, Always) is null);

        // Partitioning §6.10: HandoverBlocked is not a skip, it is a direction flip - the counterpart
        // moves instead, even though it is the more expensive (client-bearing) side. Without the block
        // this exact fixture is the "pinned" case above, where partition 1 moves.
        static bool BlocksOne(ulong id) => id == 1;
        Move flipped = ChooseMove(Report([At(1, 0)]), Report([At(2, 2000, 100, 1, 0, [77])]),
            envelope, none, Never, null, null, BlocksOne)!.Value;
        Assert(flipped.Moving.Id == 2 && !flipped.ToTarget);
        // Both sides blocked: the co-location escalates as an incident marker - never a transfer, and
        // never a silent null that would read as "co-located" to the caller.
        Move stuck = ChooseMove(Report([At(1, 0)]), Report([At(2, 2000, 100, 1, 0, [77])]),
            envelope, none, Never, null, null, Always)!.Value;
        Assert(stuck.Reason == "co-location-blocked");
        // The transient failure backoff must NOT flip direction - it skips the pass, exactly as before,
        // so the home side stays stable while a node hiccups.
        Assert(ChooseMove(Report([At(1, 0)]), Report([At(2, 2000, 100, 1, 0, [77])]),
            envelope, none, BlocksOne) is null);

        // #1243 (octahedron-1191 partition 214): a blocked member that SHARES its side with a free one
        // still makes that side the home. Heavy 1 and bridge 2 on the source, blocked 3 on the target;
        // 2 is in reach of both, 1 and 3 are not in reach of each other. The flip carried 2 to 3; the next
        // pass priced the source as home again and, 3 being blocked but 2 not, carried 2 back - forever.
        static bool BlocksThree(ulong id) => id == 3;
        PartitionInfo heavy = At(1, 0, 100, 38, 20000), bridge = At(2, 3500, 100, 5, 5000), stuckSmall = At(3, 7000, 100, 3, 500);
        List<PartitionInfo> onSource = [heavy, bridge], onTarget = [stuckSmall];
        Dictionary<ulong, int> moves = [];
        for (int pass = 0; pass < 10; pass++)
        {
            if (ChooseMove(Report([.. onSource]), Report([.. onTarget]), envelope, none, Never, null, null, BlocksThree) is not { } step)
                break;
            Assert(step.Moving.Id != 3 && step.Reason == "co-location");
            moves[step.Moving.Id] = moves.GetValueOrDefault(step.Moving.Id) + 1;
            (step.ToTarget ? onSource : onTarget).Remove(step.Moving);
            (step.ToTarget ? onTarget : onSource).Add(step.Moving);
        }
        // Converged on the blocked member's node, every free member moved exactly once.
        Assert(onSource.Count == 0 && onTarget.Count == 3);
        Assert(moves.Count == 2 && moves.Values.All(n => n == 1));
        // The decision says why it went against the price.
        Move againstPrice = ChooseMove(Report([heavy]), Report([bridge, stuckSmall]), envelope, none, Never, null, null, BlocksThree)!.Value;
        Assert(againstPrice.Moving.Id == 1 && againstPrice.ToTarget && againstPrice.Why.EndsWith("; HandoverBlocked 3 -> home target"));
        // Blocked members on BOTH sides cannot co-locate: an incident, not a shuttle of the free member.
        static bool BlocksOneAndThree(ulong id) => id is 1 or 3;
        Assert(ChooseMove(Report([heavy]), Report([bridge, stuckSmall]), envelope, none, Never, null, null, BlocksOneAndThree)!.Value.Reason
            == "co-location-blocked");

        // ===== Global load balancing (Docs/GlobalLoadBalancing.md) =====
        static (PartitionReport Report, string Node) N(string name, params PartitionInfo[] parts) =>
            (Report(parts), name);
        CostModel cm = new CostModel();

        // Converged: the hottest node's relative excess over the mean of the others is inside the
        // single tunable, so nothing moves - however uneven the counts look.
        Assert(ChooseGlobalRebalance(
            [N("a", At(1, 0, 100, 1, 10000)), N("b", At(2, 100000, 100, 1, 9000)),
             N("c", At(3, 200000, 100, 1, 9500))], cm, none, Never) is null);

        // Above tolerance the winner maximizes peak reduction PER UNIT OF LAG: the two-partition
        // 79k space cluster (no member holding a cost majority - the old guard would have skipped
        // it) beats both the giant blob (huge freeze) and the pebble (tiny gain). This is the live
        // w23 fixture from findings/node2-imbalance-two-blockers-not-the-margin.md.
        RebalancePlan packed = ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 324, 466000), At(2, 100000, 100, 20, 33498),
                    At(3, 101000, 100, 16, 45752), At(4, 200000, 100, 1, 1204)),
             N("b", At(5, 300000, 100, 1, 93000)), N("c", At(6, 400000, 100, 1, 8000)),
             N("d", At(7, 500000, 100, 1, 6000))], cm, none, Never)!.Value;
        Assert(packed.ComponentLoad == 33498 + 45752 && packed.Members.Length == 2 && packed.Source == "h");
        // The target is the optimizer's PLAN target: the cluster joins "c" because the plan also
        // vacates c's own 8000 elsewhere (minimizing the sorted load vector, not just the peak).
        // Paced mode only ever executes the plan moves that reduce over-optimal load, so the
        // residual within-tolerance shuffles the plan sketches are never actually paid for.
        Assert(packed.Target == "c");

        // No size cap and no freeze budget: the 79k winner above is over three times the old
        // 25000-PCU component cap. A component moves whenever it is the best strict peak reducer.

        // No candidate strictly lowers the peak: converged at the optimum even above tolerance.
        // Moving the 10000 blob anywhere lands a new peak at or above the old one - an atomic
        // component larger than everything else together is mathematically unbalanceable, and the
        // chooser says so by staying idle instead of shuffling it between nodes.
        Assert(ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 10000)), N("b", At(2, 100000, 100, 1, 9000)),
             N("c", At(3, 200000, 100, 1, 50))], cm, none, Never) is null);

        // Pebble churn stays dead: shuffling equal crumbs cannot strictly lower the peak.
        Assert(ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 2000), At(2, 100000, 100, 1, 2000), At(3, 200000, 100, 1, 2000)),
             N("b", At(4, 300000, 100, 1, 5000)), N("c", At(5, 400000, 100, 1, 4000))],
            cm, none, Never) is null);

        // A component already pinned by an in-flight migration is not re-initiated.
        Assert(ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 20000)), N("b", At(2, 100000, 100, 1, 100)),
             N("c", At(3, 200000, 100, 1, 100))], cm, none, Never, null, _ => true) is null);

        // The achievable-optimum gate, the second use of the single tunable: a node carrying an
        // atomic 400k blob plus one 1204-PCU pebble reads excess ~3.2 forever, but the optimal
        // assignment is only ~0.03 excess-points better - shedding the pebble buys nothing a player
        // could feel, so the balancer idles instead of taxing the cluster with a migration a minute
        // (w23 2026-08-24 17:01, Visitors accreting on the blob node as fast as they were shed).
        Assert(ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 300, 400000), At(2, 100000, 100, 1, 1204)),
             N("b", At(3, 200000, 100, 1, 95000)), N("c", At(4, 300000, 100, 1, 95000)),
             N("d", At(5, 400000, 100, 1, 95000))], cm, none, Never) is null);

        // T-0074: a partition in flight (a ship under way, MaxMemberSpeed above InFlightSpeed) is
        // not a placement unit - its load leaves with the ship and co-location re-homes it wherever
        // it lands. The over-loaded node sheds its stationary 20000 partition; when that partition
        // is flying it sheds nothing, and when a stationary sibling exists that one goes instead.
        // (b and c carry 15000 each so the 20000 partition can never move without raising the
        // peak: the 9000 one is the only legal mover.)
        Assert(ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 20000), At(4, 50000, 100, 1, 9000)), N("b", At(2, 100000, 100, 1, 15000)),
             N("c", At(3, 200000, 100, 1, 15000))], cm, none, Never)!.Value.Members.Single().Id == 4);
        Assert(ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 20000), At(4, 50000, 100, 1, 9000) with { MaxMemberSpeed = DirectGateway.InFlightSpeed + 1 }),
             N("b", At(2, 100000, 100, 1, 15000)), N("c", At(3, 200000, 100, 1, 15000))], cm, none, Never) is null);
        Assert(ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 20000), At(4, 50000, 100, 1, 9000) with { MaxMemberSpeed = 100 }, At(5, 60000, 100, 1, 9000)),
             N("b", At(2, 100000, 100, 1, 15000)), N("c", At(3, 200000, 100, 1, 15000))], cm, none, Never)!.Value.Members.Single().Id == 5);
        // T-0141 (2026-09-15): the exemption is for the SHIP, not for a whole lattice cluster one
        // ship is leaving. A 31-member partition with a moving member is still a placement unit
        // (its 30 parked members' load stays); above InFlightMaxMembers it is planned, with
        // InFlightMaxMembers = 0 (the T-0074 rule) it is not.
        Assert(ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 20000), At(4, 50000, 100, 31, 9000) with { MaxMemberSpeed = 100 }),
             N("b", At(2, 100000, 100, 1, 15000)), N("c", At(3, 200000, 100, 1, 15000))], cm, none, Never)!.Value.Members.Single().Id == 4);
        Assert(ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 20000), At(4, 50000, 100, 31, 9000) with { MaxMemberSpeed = 100 }),
             N("b", At(2, 100000, 100, 1, 15000)), N("c", At(3, 200000, 100, 1, 15000))],
            cm with { InFlightMaxMembers = 0 }, none, Never) is null);
        // T-0074 (2026-09-08): a component carrying a client is client-bound for the load
        // dimension - the same shape with the 9000 partition carrying a routed client plans
        // nothing, a client-free stationary sibling goes instead, and once the CLIENT dimension is
        // out of balance (three of three clients on one node) the client-bearing ones move.
        Assert(ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 20000), At(4, 50000, 100, 1, 9000, [77])),
             N("b", At(2, 100000, 100, 1, 15000)), N("c", At(3, 200000, 100, 1, 15000))], cm, none, Never) is null);
        Assert(ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 20000), At(4, 50000, 100, 1, 9000, [77]), At(5, 60000, 100, 1, 9000)),
             N("b", At(2, 100000, 100, 1, 15000)), N("c", At(3, 200000, 100, 1, 15000))], cm, none, Never)!.Value.Members.Single().Id == 5);
        RebalancePlan clientBound = ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 20000), At(4, 50000, 100, 1, 9000, [77]), At(5, 60000, 100, 1, 9000, [78]),
                    At(6, 70000, 100, 1, 9000, [79])),
             N("b", At(2, 100000, 100, 1, 15000)), N("c", At(3, 200000, 100, 1, 15000))], cm, none, Never)!.Value;
        Assert(clientBound.Members.Single().Id is 4 or 5 or 6);
        // The release is per node: with b above the even split (2 of 3 clients) and h at it, b's
        // client-bearing 9000s are movable and one of them goes; h's stays bound (a second pass
        // on the shape with b relieved plans nothing, although h is still the peak).
        RebalancePlan perNode = ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 20000), At(4, 50000, 100, 1, 9000, [77])),
             N("b", At(2, 100000, 100, 1, 15000), At(35, 110000, 100, 1, 9000, [78]),
                    At(36, 120000, 100, 1, 9000, [79])),
             N("c", At(3, 200000, 100, 1, 15000))], cm, none, Never)!.Value;
        Assert(perNode.Source == "b" && perNode.Members.Single().Id is 35 or 36);
        Assert(ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 20000), At(4, 50000, 100, 1, 9000, [77])),
             N("b", At(2, 100000, 100, 1, 15000), At(35, 110000, 100, 1, 9000, [78])),
             N("c", At(3, 200000, 100, 1, 15000), At(36, 210000, 100, 1, 9000, [79]))], cm, none, Never) is null);
        // T-0385 (2026-10-02): the client rule is for the pilot's SHIP, not for the base the
        // pilots stand on. A 31-member client-bearing component on a node at the even client split
        // is still a load unit and is planned as a LOAD move; with ClientBoundMaxMembers = 0 (the
        // T-0074 rule) it is not.
        // T-0391: the default is 0 now; the T-0385 rule is the override 30.
        RebalancePlan crowd = ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 20000), At(4, 50000, 100, 31, 9000, [77])),
             N("b", At(2, 100000, 100, 1, 15000)), N("c", At(3, 200000, 100, 1, 15000))], cm with { ClientBoundMaxMembers = 30 }, none, Never)!.Value;
        Assert(crowd.Members.Single().Id == 4 && crowd.Kind == RebalanceKind.Load);
        Assert(ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 20000), At(4, 50000, 100, 31, 9000, [77])),
             N("b", At(2, 100000, 100, 1, 15000)), N("c", At(3, 200000, 100, 1, 15000))],
            cm with { ClientBoundMaxMembers = 0 }, none, Never) is null);

        // ===== The idle-cluster full plan (Viktor's approximate startup optimizer) =====
        // Everything piled on one node - the cold-start shape - plans out in ONE pass to the
        // optimal packing for these weights (peak 10000 across three nodes, provably 4 moves
        // minimum), biggest mover first. The stochastic optimizer is seeded, so this is stable.
        RebalancePlan[] startup = ChooseRebalancePlans(
            [N("h", At(1, 0, 100, 1, 7000), At(2, 10000, 100, 1, 6000), At(3, 20000, 100, 1, 5000),
                    At(4, 30000, 100, 1, 4000), At(5, 40000, 100, 1, 3000), At(6, 50000, 100, 1, 3000)),
             N("b"), N("c")], cm, none, Never, fullPlan: true);
        Assert(startup.Length == 4 && startup[^1].NewPeak == 10000);
        Assert(startup[0].ComponentLoad == 6000);   // big movers first, 7000 and one 3000 stay home
        Assert(startup.Sum(p => p.ComponentLoad) == 18000);

        // Full plan on the w23 fixture: the blob stays (no move can lower that peak), the space
        // cluster and the pebble leave for the emptier nodes, everything else stays home because
        // moving it would be cost without gain, and the trajectory ends at the blob-alone optimum.
        RebalancePlan[] w23 = ChooseRebalancePlans(
            [N("h", At(1, 0, 100, 324, 466000), At(2, 100000, 100, 20, 33498),
                    At(3, 101000, 100, 16, 45752), At(4, 200000, 100, 1, 1204)),
             N("b", At(5, 300000, 100, 1, 93000)), N("c", At(6, 400000, 100, 1, 8000)),
             N("d", At(7, 500000, 100, 1, 6000))], cm, none, Never, fullPlan: true);
        // Idle mode pays for third-order polish too (8000 relocates so the cluster's target node
        // ends lower) - migration is free with nobody connected.
        Assert(w23.Length == 3 && w23[0].ComponentLoad == 79250 && w23[0].Source == "h");
        Assert(w23[^1].NewPeak == 466000);

        // A converged world plans nothing in full-plan mode either.
        Assert(ChooseRebalancePlans(
            [N("a", At(1, 0, 100, 1, 10000)), N("b", At(2, 100000, 100, 1, 9000)),
             N("c", At(3, 200000, 100, 1, 9500))], cm, none, Never, fullPlan: true).Length == 0);

        // ===== Entity-gap placement (Plan/EntityGapPlacement.md) =====
        // The w23 blob-gluing case: two partitions whose bounding SPHERES overlap but whose
        // CONTENT is 10.8 km apart (12328/12322, 2026-08-24). With member geometry reported they
        // are separate components - no co-location move - where the sphere test manufactured a
        // false 443k atomic unit.
        static PartitionInfo AtM(ulong id, double x, double radius, double[][] memberSpheres,
            double maxSpeed = 0, int pcu = 1000) =>
            new(id, memberSpheres.Length, true, new PartitionSphere(x, 0, 0, radius), maxSpeed, [],
                pcu, 400L * pcu + 8_000L * memberSpheres.Length + 2_000, MemberSpheres: memberSpheres);
        PartitionInfo spreadA = AtM(1, 10000, 20000,
            [[0, 0, 0, 100, 0], [19000, 0, 0, 100, 0]]);          // content spans 0..19km
        PartitionInfo farB = AtM(2, 25000, 10000, [[30000, 0, 0, 100, 0]]);
        Assert(25000 - 10000 - (10000 + 20000) < 0);               // fixture sanity: spheres overlap...
        Assert(ChooseMove(Report([spreadA]), Report([farB]), envelope, none, Never) is null);
        // ...but content 1.8 km apart IS a genuine violation, sphere inflation or not.
        PartitionInfo nearB = AtM(3, 25000, 10000, [[20900, 0, 0, 100, 0]]);
        Assert(ChooseMove(Report([spreadA]), Report([nearB]), envelope, none, Never) is not null);
        // Velocity applies per member pair with the mover's relocation lead (floor 5 s): the same
        // 4.8 km separation is safe when static and adjacent when both members close at 100 m/s.
        PartitionInfo staticA = AtM(4, 0, 100, [[0, 0, 0, 100, 0]]);
        PartitionInfo staticB = AtM(5, 5000, 100, [[5000, 0, 0, 100, 0]]);
        Assert(ChooseMove(Report([staticA]), Report([staticB]), envelope, none, Never) is null);
        PartitionInfo movingA = AtM(6, 0, 100, [[0, 0, 0, 100, 100]], maxSpeed: 100);
        PartitionInfo movingB = AtM(7, 5000, 100, [[5000, 0, 0, 100, 100]], maxSpeed: 100);
        Assert(ChooseMove(Report([movingA]), Report([movingB]), envelope, none, Never) is not null);
        // T-0141: with the velocity VECTOR reported, only CLOSING speed shortens the gap. The same
        // pair approaching head-on is adjacent; flying apart, or side by side on parallel courses,
        // it is not - the direction-blind sum linked both, and that chained the pilots together.
        PartitionInfo closingA = AtM(12, 0, 100, [[0, 0, 0, 100, 100, 100, 0, 0]], maxSpeed: 100);
        PartitionInfo closingB = AtM(13, 5000, 100, [[5000, 0, 0, 100, 100, -100, 0, 0]], maxSpeed: 100);
        Assert(ChooseMove(Report([closingA]), Report([closingB]), envelope, none, Never) is not null);
        PartitionInfo recedingA = AtM(14, 0, 100, [[0, 0, 0, 100, 100, -100, 0, 0]], maxSpeed: 100);
        PartitionInfo recedingB = AtM(15, 5000, 100, [[5000, 0, 0, 100, 100, 100, 0, 0]], maxSpeed: 100);
        Assert(ChooseMove(Report([recedingA]), Report([recedingB]), envelope, none, Never) is null);
        PartitionInfo parallelA = AtM(16, 0, 100, [[0, 0, 0, 100, 100, 0, 100, 0]], maxSpeed: 100);
        PartitionInfo parallelB = AtM(17, 5000, 100, [[5000, 0, 0, 100, 100, 0, 100, 0]], maxSpeed: 100);
        Assert(ChooseMove(Report([parallelA]), Report([parallelB]), envelope, none, Never) is null);
        // A mixed pair (one old 5-wide row) falls back to the speed sum: still adjacent.
        Assert(ChooseMove(Report([recedingA]), Report([movingB]), envelope, none, Never) is not null);
        // A flying member must not inflate the whole partition's reach: a static base member 6 km
        // from a static neighbour stays separate even though a fast mover sits INSIDE the base
        // partition far from the boundary pair.
        PartitionInfo baseWithShip = AtM(8, 3000, 4000,
            [[0, 0, 0, 100, 0], [6000, 0, 0, 100, 100]], maxSpeed: 100);
        PartitionInfo quietNeighbour = AtM(9, -6100, 100, [[-6100, 0, 0, 100, 0]]);
        Assert(ChooseMove(Report([baseWithShip]), Report([quietNeighbour]), envelope, none, Never) is null);
        // Reports without member geometry (older node build) keep the bounding-sphere behavior.
        Assert(ChooseMove(Report([At(10, 0, 20000)]), Report([At(11, 25000, 10000)]),
            envelope, none, Never) is not null);

        // Audience-weighted destinations: the import stall lands on every client of the RECEIVING
        // node (w23 20:18: a 126k migration dropped its 8-client target to sim 0.02 for ~4 s), so
        // between two equally loaded targets the mover prefers the client-quiet one. Same fixture,
        // clients swapped, the choice follows the quiet node - and a lone candidate still moves.
        // The optimal plan sends one 5000 to b and one to c either way; the paced mover's
        // per-move score decides which fires FIRST, and audience weighting steers it to the
        // client-quiet destination.
        RebalancePlan quietTarget = ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 15000), At(2, 10000, 100, 1, 5000), At(6, 20000, 100, 1, 5000)),
             N("b", At(3, 100000, 100, 1, 3000, [77])), N("c", At(4, 200000, 100, 1, 3000))],
            cm, [77], Never)!.Value;
        Assert(quietTarget.ComponentLoad == 5000 && quietTarget.Target == "c");
        RebalancePlan quietSwapped = ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 1, 15000), At(2, 10000, 100, 1, 5000), At(6, 20000, 100, 1, 5000)),
             N("b", At(3, 100000, 100, 1, 3000)), N("c", At(4, 200000, 100, 1, 3000, [77]))],
            cm, [77], Never)!.Value;
        Assert(quietSwapped.ComponentLoad == 5000 && quietSwapped.Target == "b");
        // The weighting is destination-only: a peak node that is ALSO the busiest (w23 20:33,
        // node-1 at 503k with 17 of 20 clients after a crash pile-up) must still be drained -
        // its own clients never enter the cost term, only the receiving node's do.
        RebalancePlan busyPeak = ChooseGlobalRebalance(
            [N("h", At(21, 0, 100, 1, 15000, [77]), At(22, 10000, 100, 1, 5000)),
             N("b", At(23, 100000, 100, 1, 100)), N("c", At(24, 200000, 100, 1, 100))],
            cm, [77], Never)!.Value;
        Assert(busyPeak.Source == "h" && busyPeak.ComponentLoad == 5000);
        // Clients are a balance dimension of their own (Viktor 2026-08-24 evening): a PCU-balanced
        // cluster with every player piled on one node still plans a move - the cheapest
        // client-bearing crumb goes to the quiet node, tolerated because the resulting PCU excess
        // stays inside the single tunable. With the clients already spread, the same cluster is
        // converged and plans nothing.
        RebalancePlan clientPile = ChooseGlobalRebalance(
            [N("h", At(31, 0, 100, 1, 20000), At(32, 10000, 100, 1, 1000, [80]),
                    At(33, 20000, 100, 1, 1000, [81])),
             N("b", At(34, 100000, 100, 1, 20000), At(35, 110000, 100, 1, 1000),
                    At(36, 120000, 100, 1, 1000))],
            cm, [80, 81], Never)!.Value;
        Assert(clientPile.Source == "h" && clientPile.Target == "b" && clientPile.ComponentLoad == 1000);
        Assert(ChooseGlobalRebalance(
            [N("h", At(31, 0, 100, 1, 20000), At(32, 10000, 100, 1, 1000, [80]),
                    At(33, 20000, 100, 1, 1000)),
             N("b", At(34, 100000, 100, 1, 20000), At(35, 110000, 100, 1, 1000, [81]),
                    At(36, 120000, 100, 1, 1000))],
            cm, [80, 81], Never) is null);
        // Per-node-disjoint paced pass (Viktor 2026-08-24, Option 1): one pass may start several
        // migrations as long as every node is in at most ONE - two over-loaded nodes drain to two
        // quiet nodes concurrently; a node already busy with an in-flight migration (busyNodes)
        // takes its pair out of this pass.
        RebalancePlan[] pair = ChooseRebalancePlans(
            [N("h1", At(41, 0, 100, 1, 20000), At(42, 10000, 100, 1, 5000)),
             N("h2", At(43, 100000, 100, 1, 20000), At(44, 110000, 100, 1, 5000)),
             N("e1", At(45, 200000, 100, 1, 100)), N("e2", At(46, 300000, 100, 1, 100))],
            cm, none, Never);
        Assert(pair.Length == 2 && pair.All(p => p.ComponentLoad == 5000));
        Assert(pair.SelectMany(p => new[] { p.Source, p.Target }).Distinct().Count() == 4);
        RebalancePlan[] withBusy = ChooseRebalancePlans(
            [N("h1", At(41, 0, 100, 1, 20000), At(42, 10000, 100, 1, 5000)),
             N("h2", At(43, 100000, 100, 1, 20000), At(44, 110000, 100, 1, 5000)),
             N("e1", At(45, 200000, 100, 1, 100)), N("e2", At(46, 300000, 100, 1, 100))],
            cm, none, Never, busyNodes: new HashSet<string> { "h2" });
        Assert(withBusy.Length == 1 && withBusy[0].Source == "h1");

        // Secondary imbalance: the peak node sits at its atomic floor - by the PEAK metric the
        // cluster is "done" - while a rotation-stranded 82k cluster loads one node to 177k next to
        // a 2,400-PCU one (live w23 18:18). The per-node deviation gate sees it, and the paced mover
        // may source from ANY over-loaded node, moving the 82k to the empty node without touching
        // the peak (OldPeak == NewPeak is legal for a non-peak move).
        RebalancePlan secondary = ChooseGlobalRebalance(
            [N("h", At(1, 0, 100, 300, 435000)),
             N("b", At(2, 100000, 100, 5, 95000), At(3, 200000, 100, 5, 82000)),
             N("c", At(4, 300000, 100, 1, 2400)), N("d", At(5, 400000, 100, 1, 102000))],
            cm, none, Never)!.Value;
        Assert(secondary.Source == "b" && secondary.Target == "c" && secondary.ComponentLoad == 82000);
        Assert(secondary.OldPeak == secondary.NewPeak);

        // ===== CLUSTER-SIM-COST: measured load replaces PCU when every node reports it =====
        static PartitionInfo AtL(ulong id, double x, int pcu, long simLoad, ulong[]? clients = null) =>
            new(id, 1, true, new PartitionSphere(x, 0, 0, 100), 0, clients ?? [], pcu,
                400L * pcu + 10_000, simLoad);
        static (PartitionReport Report, string Node) NS(string name, params PartitionInfo[] parts) =>
            (new PartitionReport(3000, 1000, parts, 0, 50, SimLoadSupported: true), name);
        // PCU-balanced (20k per node) but measured-cost-lopsided: the parked 20k-PCU museum reads
        // ~500 us/s, the two piloted 10k-PCU tugs read 90k+60k; the plan moves BY MEASURED COST,
        // from the busy node, and names the unit.
        RebalancePlan measured = ChooseGlobalRebalance(
            [NS("busy", AtL(50, 0, 10000, 90_000), AtL(51, 10000, 10000, 60_000)),
             NS("parked", AtL(52, 100000, 20000, 500)),
             NS("idle", AtL(53, 200000, 20000, 400))], cm, none, Never)!.Value;
        Assert(measured.Source == "busy" && measured.LoadUnit == "sim-us/s"
            && (measured.ComponentLoad == 90_000 || measured.ComponentLoad == 60_000));
        // One pre-field node in the pass drops the WHOLE pass back to PCU (the units are not
        // commensurable): the same shape with one legacy report is PCU-balanced, so nothing moves.
        Assert(ChooseGlobalRebalance(
            [NS("busy", AtL(50, 0, 10000, 90_000), AtL(51, 10000, 10000, 60_000)),
             NS("parked", AtL(52, 100000, 20000, 500)),
             N("legacy", At(53, 200000, 100, 1, 20000))], cm, none, Never) is null);

        // ===== Migration pins drive ChooseMove (the execution half) =====
        static MigrationSide PinnedTarget(ulong _) => MigrationSide.Target;

        // A fully co-located component pinned to the target moves minority-first, floored at
        // envelope urgency, as reason balance-migration - the move the old majority guard forbade.
        Move migrate1 = ChooseMove(Report([At(1, 0, 100, 1, 30000), At(2, 1000, 100, 1, 3000)]),
            Report([]), envelope, none, Never, null, null, null, PinnedTarget)!.Value;
        Assert(migrate1.Reason == "balance-migration" && migrate1.ToTarget && migrate1.Gap == envelope);
        // Unpinned, the same mono-located component stays put.
        Assert(ChooseMove(Report([At(1, 0, 100, 1, 30000), At(2, 1000, 100, 1, 3000)]),
            Report([]), envelope, none, Never) is null);
        // A real cross-node violation always outranks a migration move (its gap is under the
        // envelope by definition, the migration's is floored at it).
        Move urgent = ChooseMove(
            Report([At(1, 0, 100, 1, 30000), At(10, 50000), At(11, 52000)]),
            Report([At(12, 51000)]), envelope, none, Never, null, null, null,
            id => id == 1 ? MigrationSide.Target : MigrationSide.None)!.Value;
        Assert(urgent.Reason == "co-location" && urgent.Moving.Id == 12);
        // Members already arrived on the pinned side are done; nothing bounces back.
        Assert(ChooseMove(Report([]), Report([At(1, 0, 100, 1, 30000)]),
            envelope, none, Never, null, null, null, PinnedTarget) is null);
        // A component pinned to a node outside this pair is left entirely alone.
        Assert(ChooseMove(Report([At(1, 0)]), Report([At(2, 2000)]),
            envelope, none, Never, null, null, null, _ => MigrationSide.Elsewhere) is null);
        // A straddling component's home follows the pin, overriding the cost majority.
        Move pinnedHome = ChooseMove(Report([At(1, 0, 100, 10, 50000)]), Report([At(2, 2000)]),
            envelope, none, Never, null, null, null, PinnedTarget)!.Value;
        Assert(pinnedHome.Reason == "balance-migration" && pinnedHome.Moving.Id == 1 && pinnedHome.ToTarget);

        // ===== T-0249: a pinned migration must not carry a client away from its neighbours =====
        // w33 2026-09-26 01:46: base P517 (clientless, big) overlapping p3641 (Viktor's client), both
        // pinned node-5 -> node-3. P517's capture was refused in a loop; p3641 went alone and his client
        // lost every grid for 64 s. Here 517 = the base at x=0, 3641 = the client's partition 1 km away.
        PartitionInfo baseP = At(517, 0, 1400, 300, 110000);
        PartitionInfo shipP = At(3641, 1000, 300, 33, 2000, [77]);
        // Clients last: the client-less base goes first, the client's partition waits.
        Move baseFirst = ChooseMove(Report([baseP, shipP]), Report([]), envelope, none, Never,
            null, null, null, PinnedTarget)!.Value;
        Assert(baseFirst.Moving.Id == 517 && baseFirst.ToTarget && baseFirst.Reason == "balance-migration");
        // Stall: the base is in handover backoff, so the client's partition does not go without it.
        Assert(ChooseMove(Report([baseP, shipP]), Report([]), envelope, none, id => id == 517,
            null, null, null, PinnedTarget) is null);
        // Once the base has arrived, the client's partition follows (the split lasts one pass).
        Move shipFollows = ChooseMove(Report([shipP]), Report([baseP]), envelope, none, Never,
            null, null, null, PinnedTarget)!.Value;
        Assert(shipFollows.Moving.Id == 3641 && shipFollows.ToTarget);
        // A validation-blocked base ends the pin: an unsplit component stays together where it is...
        Assert(ChooseMove(Report([baseP, shipP]), Report([]), envelope, none, Never,
            null, null, id => id == 517, PinnedTarget) is null);
        // ...and an already split one is reunited on the blocked base's node, not left apart.
        Move reunite = ChooseMove(Report([baseP]), Report([shipP]), envelope, none, Never,
            null, null, id => id == 517, PinnedTarget)!.Value;
        Assert(reunite.Moving.Id == 3641 && !reunite.ToTarget && reunite.Reason == "co-location");

        // ===== T-0141 third attempt: co-location envelope hysteresis =====
        // Geometry: spheres of radius 100 at x, so the gap between partitions at 0 and X is X-200.
        // SyncDistance 3000, Margin 1000, envelope 4000, and engage = 4000 * Fraction.
        CostModel plain = new();
        CostModel hyst = new(ColocationEngageFraction: 0.875);   // engage at 3500 m   // engage at 3500 m
        Assert(plain.ColocationEngageFraction == 1.0);           // ships INERT by default

        // A cross-node pair at gap 3600 - inside the envelope, outside the engage threshold.
        PartitionReport left = Report([At(1, 0, 100, 10, 50000)]);
        PartitionReport right = Report([At(2, 3800)]);
        // Today, and at the default fraction, it is dragged together.
        Assert(ChooseMove(left, right, envelope, none, Never, plain) is { Reason: "co-location" });
        // With hysteresis it is left alone: "might close" no longer becomes permanent placement.
        Assert(ChooseMove(left, right, envelope, none, Never, hyst) is null);

        // THE INVARIANT. A cross-node pair closer than SyncDistance is the 3000 m sphere-gap
        // violation that killed attempt #1, and hysteresis must never leave one standing. Gap 2800.
        Assert(ChooseMove(Report([At(1, 0, 100, 10, 50000)]), Report([At(2, 3000)]),
            envelope, none, Never, hyst) is { Reason: "co-location" });
        // The bound that guarantees it: engage must stay above SyncDistance. At the deployed 3000 +
        // 1000 geometry that is any fraction above 0.75, and 0.875 leaves 500 m of margin.
        Assert(envelope * hyst.ColocationEngageFraction > 3000);

        // SAME-NODE pairs keep the full envelope, which is what makes placement the memory: an
        // already-co-located component is not torn apart by the narrower threshold. Both at gap
        // 3600 from each other on the SOURCE, with a third partition on the target pulling one of
        // them - the pair must still be seen as one component and priced as one.
        Move together = ChooseMove(Report([At(1, 0, 100, 10, 50000), At(2, 3800, 100, 10, 50000)]),
            Report([At(3, 5000)]), envelope, none, Never, hyst)!.Value;
        Assert(together.ComponentSize == 3 && together.Moving.Id == 3);

        // And it does not oscillate, which is this section's standing property: re-running the
        // decision on the post-move placement must produce nothing.
        Assert(ChooseMove(Report([At(1, 0, 100, 10, 50000), At(2, 3800, 100, 10, 50000), At(3, 5000)]),
            Report([]), envelope, none, Never, hyst) is null);

        // #1304: the balancer plans on the same threshold. Two 50000-PCU partitions 3600 m apart on n1 (inside the
        // envelope, outside engage) and an empty n2: at 1.0 they are one unit that cannot move without raising the peak,
        // with the hysteresis each is a unit of its own and one moves.
        (PartitionReport, string)[] lane = [(Report([At(1, 0, 100, 10, 50000), At(2, 3800, 100, 10, 50000)]), "n1"),
            (Report([At(9, 100000, 100, 1, 1)]), "n2")];
        Assert(ChooseRebalancePlans(lane, plain, none, Never) is []);
        Assert(ChooseRebalancePlans(lane, hyst, none, Never) is [{ Members: [_], Source: "n1", Target: "n2" }]);
        // A pin carries its unit only. At 1.0 the pin on 2 carries the whole envelope component (1 first); with the
        // hysteresis it carries 2 and leaves 1, and once 2 has arrived nothing drags it back.
        static MigrationSide PinTwo(ulong id) => id == 2 ? MigrationSide.Target : MigrationSide.None;
        PartitionReport n1 = Report([At(1, 0, 100, 10, 50000), At(2, 3800, 100, 10, 50000)]), n2 = Report([At(9, 100000, 100, 1, 1)]);
        Assert(ChooseMove(n1, n2, envelope, none, Never, plain, null, null, PinTwo) is { Moving.Id: 1, Reason: "balance-migration" });
        Assert(ChooseMove(n1, n2, envelope, none, Never, hyst, null, null, PinTwo) is { Moving.Id: 2, Reason: "balance-migration" });
        PartitionReport n1After = Report([At(1, 0, 100, 10, 50000)]), n2After = Report([At(2, 3800, 100, 10, 50000), At(9, 100000, 100, 1, 1)]);
        Assert(ChooseMove(n1After, n2After, envelope, none, Never, hyst, null, null, PinTwo) is null);
        Assert(ChooseMove(n1After, n2After, envelope, none, Never, hyst) is null);
        // And the gateway runs it: the deployed model engages at 3500 m, still above SyncDistance.
        Assert(CostModel.Deployed().ColocationEngageFraction == 0.875);
    }

    /// <summary>T-0235: a node an operator /handover drained within the window is draining; one
    /// whose last drain move is older is not (the rotator re-arms it with every move).</summary>
    /// <summary>
    /// T-0296 r3: the end of a switch must end the switch on every exit. The player binding re-attach inside it times
    /// out when the World Authority is replaced or away while the switch completes; that exception left
    /// SwitchingBackend set (the relay buffers the client both ways with the age bound off), ended CompleteSwitch's
    /// loop before the rest of the crew was un-frozen, and skipped RespawnSwitch's SwitchLock release.
    /// </summary>
    private static void TestFinishSwitching()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("finish-switching: " + what); }
        object relayLock = new();
        bool switching = true, completed = false;
        Exception? reported = null;
        bool healthy;
        try
        {
            healthy = DirectGateway.FinishSwitching(relayLock, () => true,
                () => throw new TimeoutException("Timed out attaching the current player binding"), () => false,
                () => switching = false, () => completed = true, e => reported = e);
        }
        catch (TimeoutException e)
        {
            throw new InvalidOperationException($"finish-switching: the re-attach timeout escaped (switching={switching}, completion ran={completed}): {e.Message}");
        }
        Check(!switching && completed, $"the switch ended: switching={switching}, completion ran={completed}");
        Check(!healthy, "a binding that could not be re-attached ends the switch as unhealthy (the caller disconnects)");
        Check(reported is TimeoutException, "the failure is reported");

        // Controls: a healthy route with a current binding, and an unhealthy route (the binding is not asked for).
        switching = true; completed = false;
        int ensured = 0, asked = 0;
        Check(DirectGateway.FinishSwitching(relayLock, () => true, () => ensured++, () => ++asked > 1, () => switching = false,
            () => completed = true, _ => { }) && !switching && completed && ensured == 2, "a binding that turns current on the second round completes healthy");
        switching = true; completed = false; ensured = 0;
        Check(!DirectGateway.FinishSwitching(relayLock, () => false, () => ensured++, () => false, () => switching = false,
            () => completed = true, _ => { }) && !switching && completed && ensured == 0, "an unhealthy route ends the switch without asking for the binding");
        Console.WriteLine("finish-switching: a failed binding re-attach ends the switch as unhealthy instead of leaving it switching");
    }

    private static void TestDrainSources()
    {
        DateTime now = new(2026, 9, 24, 12, 42, 17, DateTimeKind.Utc);
        TimeSpan window = TimeSpan.FromSeconds(90);
        var sources = new Dictionary<string, DateTime>
        {
            ["node-3"] = now.AddSeconds(-3),     // the drain's last move 3 s ago
            ["node-1"] = now.AddSeconds(-91),    // an older drain, finished
            ["node-5"] = now.AddSeconds(-89.9),
        };
        string[] draining = DirectGateway.RecentDrainSources(sources, now, window);
        Assert(draining.SequenceEqual(["node-3", "node-5"]));
        Assert(DirectGateway.RecentDrainSources([], now, window).Length == 0);
        // T-0296 r3: the drain catch-up of a closed node must not pick a retiring node, which is lightest BECAUSE it drains.
        (string, long)[] serving = [("node-2", 90_000), ("node-4", 12_000), ("node-6", 60_000)];
        if (DirectGateway.LightestDrainTarget(serving, new HashSet<string> { "node-4" }) != "node-6")
            throw new InvalidOperationException("drain-sources: the catch-up drains into the retiring node-4 instead of node-6");
        if (DirectGateway.LightestDrainTarget(serving, new HashSet<string>()) != "node-4")
            throw new InvalidOperationException("drain-sources: with nothing retiring the lightest node is the target");
        if (DirectGateway.LightestDrainTarget([("node-4", 12_000)], new HashSet<string> { "node-4" }) != null)
            throw new InvalidOperationException("drain-sources: only a retiring node left means no target (the force drain decides)");
    }

    private static void Assert(bool value)
    {
        if (!value)
            throw new InvalidOperationException("self-test failed");
    }

    private static void AssertThrows(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("self-test failed: expected invalid data");
    }

    private static void AssertArgument(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("self-test failed: expected invalid argument");
    }
}
