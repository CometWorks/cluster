using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// T-0401: the import-Add mark and timers (ImportAddDiag) and GridAddTrace's import skip. Knob defaults and
	/// Normalize; the mark is set for exactly the entity being Added, on this thread, and cleared after the Add,
	/// also when the Add throws (and a hook depth left open by the throw does not leak into the next Add); the
	/// timer hooks count nothing outside an import Add; event and physics time are disjoint parts of the Add's total
	/// (physics inside an event stays in the event); the log fields keep their order. GridAddTrace.Skips: an
	/// import Add is skipped only with diag.gridAddTraceImports false, a non-import add never. No game types:
	/// plain objects stand in for entities (the grid counting needs a live MyCubeGrid and is not covered here).
	/// </summary>
	private static void ImportAddDiagRules()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("import-add-diag: " + message); }
		void Spin(double ms) { Stopwatch watch = Stopwatch.StartNew(); while (watch.Elapsed.TotalMilliseconds < ms) { } }

		Check(ClusterConfig.DefaultOf("diag.importAddTimers") == "true", "diag.importAddTimers defaults to true");
		Check(ClusterConfig.DefaultOf("diag.gridAddTraceImports") == "false", "diag.gridAddTraceImports defaults to false (skip import adds)");
		Check(ClusterConfig.DefaultOf("import.deferPhysics") == "true", "import.deferPhysics defaults to true (T-0401 fix, live A/B r121i PASS)");
		Check(ClusterConfig.DefaultOf("diag.importAddClusterTrace") == "false", "diag.importAddClusterTrace defaults to false (log-only diag)");
		foreach (string key in new[] { "diag.importAddTimers", "diag.gridAddTraceImports", "import.deferPhysics", "diag.importAddClusterTrace" })
		{
			Check(ClusterConfig.Normalize(key, "TRUE") == "true" && ClusterConfig.Normalize(key, "False") == "false", key + " normalizes true/false");
			bool refused = false;
			try { ClusterConfig.Normalize(key, "1"); } catch (ArgumentException) { refused = true; }
			Check(refused, key + "=1 must be refused");
		}

		object imported = new object();
		object other = new object();
		Check(!ImportAddDiag.InImportAdd && !ImportAddDiag.IsImportAdd(imported) && !ImportAddDiag.IsImportAdd(null), "no mark before any import Add");
		Check(!GridAddTrace.Skips(imported, false) && !GridAddTrace.Skips(imported, true), "a non-import add is traced whatever the knob");

		// The mark: exactly the entity, inside the Add only; Skips follows it and the knob.
		bool inside = false, skipOff = false, skipOn = true, skipOther = true;
		ImportAddDiag.Run(imported, null, () =>
		{
			inside = ImportAddDiag.IsImportAdd(imported) && !ImportAddDiag.IsImportAdd(other);
			skipOff = GridAddTrace.Skips(imported, traceImports: false);
			skipOn = GridAddTrace.Skips(imported, traceImports: true);
			skipOther = GridAddTrace.Skips(other, traceImports: false);
		});
		Check(inside, "the mark is on the Added entity only, during its Add");
		Check(skipOff, "an import add is skipped with diag.gridAddTraceImports=false");
		Check(!skipOn, "an import add is traced with diag.gridAddTraceImports=true");
		Check(!skipOther, "another entity added during an import Add is traced");
		Check(!ImportAddDiag.InImportAdd && !GridAddTrace.Skips(imported, false), "the mark is cleared after the Add");

		// A throwing Add (timed, with an event hook left open by the throw): mark cleared, Add counted, no depth leak.
		ImportAddSlice thrown = new ImportAddSlice();
		bool threw = false;
		try
		{
			ImportAddDiag.Run(imported, thrown, () => { ImportAddDiag.EventEnter(); throw new InvalidOperationException("add"); });
		}
		catch (InvalidOperationException e) when (e.Message == "add") { threw = true; }
		Check(threw, "the Add's exception propagates");
		Check(!ImportAddDiag.InImportAdd && !ImportAddDiag.IsImportAdd(imported), "the mark is cleared after an Add that throws");
		Check(thrown.Adds == 1 && thrown.EventCalls == 0, "a throwing Add is counted, its unfinished event is not");

		// Outside an import Add the hooks count nothing.
		ImportAddDiag.EventEnter(); ImportAddDiag.EventExit();
		ImportAddDiag.PhysicsEnter(); ImportAddDiag.PhysicsExit();
		ImportAddDiag.CountClusterCreate(); ImportAddDiag.CountClusterReorder();

		// Timed Add: physics, then the event with a nested event and physics inside it, then physics again; parts are disjoint.
		ImportAddSlice slice = new ImportAddSlice();
		ImportAddDiag.Run(imported, slice, () =>
		{
			Spin(2);
			ImportAddDiag.PhysicsEnter(); ImportAddDiag.PhysicsEnter(); Spin(3); ImportAddDiag.CountClusterCreate(); ImportAddDiag.PhysicsExit(); ImportAddDiag.PhysicsExit();
			ImportAddDiag.EventEnter(); Spin(3); ImportAddDiag.EventEnter(); ImportAddDiag.PhysicsEnter(); Spin(1); ImportAddDiag.CountClusterReorder(); ImportAddDiag.PhysicsExit(); ImportAddDiag.EventExit(); ImportAddDiag.EventExit();
			ImportAddDiag.PhysicsEnter(); Spin(1); ImportAddDiag.PhysicsExit();
		});
		Check(thrown.EventCalls == 0 && thrown.ClusterCreates == 0, "hooks outside an import Add count nothing (and not into a finished slice)");
		Check(slice.Adds == 1 && slice.EventCalls == 1 && slice.PhysicsCalls == 2, $"outermost hits only, physics inside the event not counted (adds {slice.Adds} events {slice.EventCalls} physics {slice.PhysicsCalls}); the depth from the throwing Add did not leak");
		Check(slice.ClusterCreates == 1 && slice.ClusterReorders == 1, "cluster creates and reorders counted inside the Add");
		double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
		double total = Ms(slice.AddTicks), ev = Ms(slice.EventTicks), physics = Ms(slice.PhysicsTicks);
		Check(physics >= 4 && ev >= 4 && total >= 10, $"times cover the spins (total {total:F2} event {ev:F2} physics {physics:F2})");
		Check(ev + physics <= total, $"event + physics within the total (total {total:F2} event {ev:F2} physics {physics:F2})");

		string fields = slice.Fields();
		string[] keys = Regex.Matches(fields, @" (\w+)=").Select(m => m.Groups[1].Value).ToArray();
		string expected = "addN addTotalMs addEventMs addPhysicsMs addSceneOtherMs addEventCalls addPhysicsCalls addClusterCreates addClusterReorders"
			+ " addGrids addOthers addBlocksSum addBlocksMax addFatBlocksSum addGc0 addGc1 addGc2 addGcPauseMs"
			+ " addCpuMs addBlockedMs addLockContention addEventCpuMs addPhysicsCpuMs addReorderMs addReorderMoved addCreateMs";
		Check(string.Join(" ", keys) == expected, "field order: " + fields);
		Match other3 = Regex.Match(fields, @" addTotalMs=([\d.]+) addEventMs=([\d.]+) addPhysicsMs=([\d.]+) addSceneOtherMs=(-?[\d.]+) ");
		Check(other3.Success && double.Parse(other3.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) >= 0, "addSceneOtherMs = total - event - physics, not negative: " + fields);
		Check(!ImportAddDiag.InImportAdd, "no mark left behind");

		// T-0401 reorder diag: ReorderClusters / CreateCluster timed (outermost), moves counted only inside a reorder,
		// nothing outside an import Add, and a reorder left open by a throw does not leak into the next slice.
		ImportAddDiag.ReorderEnter(); ImportAddDiag.CountReorderMove(); ImportAddDiag.ReorderExit();
		ImportAddDiag.CreateEnter(); ImportAddDiag.CreateExit();
		ImportAddSlice reorder = new ImportAddSlice();
		ImportAddDiag.Run(imported, reorder, () =>
		{
			ImportAddDiag.CountReorderMove();
			ImportAddDiag.ReorderEnter(); Spin(3);
			ImportAddDiag.CreateEnter(); Spin(1); ImportAddDiag.CreateExit();
			for (int i = 0; i < 5; i++) ImportAddDiag.CountReorderMove();
			ImportAddDiag.ReorderEnter(); ImportAddDiag.CountReorderMove(); ImportAddDiag.ReorderExit();
			ImportAddDiag.ReorderExit();
			ImportAddDiag.CreateEnter(); Spin(1); ImportAddDiag.CreateExit();
		});
		double reorderMs = reorder.ReorderTicks * 1000.0 / Stopwatch.Frequency, createMs = reorder.CreateTicks * 1000.0 / Stopwatch.Frequency;
		Check(reorder.ClusterReorders == 2 && reorder.ClusterCreates == 2 && reorder.ReorderMoved == 6,
			$"reorders 2 (nested counted), creates 2, moved 6 (the move before the reorder not counted): {reorder.ClusterReorders} {reorder.ClusterCreates} {reorder.ReorderMoved}");
		Check(reorderMs >= 4 && createMs >= 2 && reorderMs <= reorder.AddTicks * 1000.0 / Stopwatch.Frequency,
			$"reorder ms covers its spins once (outermost), create ms both creates, within the Add: reorder {reorderMs:F2} create {createMs:F2}");
		ImportAddSlice open = new ImportAddSlice();
		try { ImportAddDiag.Run(imported, open, () => { ImportAddDiag.ReorderEnter(); throw new InvalidOperationException("reorder"); }); }
		catch (InvalidOperationException e) when (e.Message == "reorder") { }
		ImportAddSlice after = new ImportAddSlice();
		ImportAddDiag.Run(imported, after, () => { ImportAddDiag.CountReorderMove(); });
		Check(after.ReorderMoved == 0 && after.ReorderTicks == 0, "a reorder left open by a throw does not leak its depth into the next slice");

		// T-0401 lock hypothesis: thread CPU clock, CPU <= wall per part, lock contention, the -1 path.
		const double Epsilon = 0.5;
		bool linux = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux);
		Check(!linux || ImportAddDiag.ThreadCpuNs() >= 0, "the thread CPU clock is available on Linux");
		if (ImportAddDiag.ThreadCpuNs() >= 0)
		{
			// Busy loop until the clock has advanced 5 ms (2 s wall at most): monotone, advancing, never ahead of the wall.
			Stopwatch wall = Stopwatch.StartNew();
			long first = ImportAddDiag.ThreadCpuNs(), last = first;
			bool monotone = true;
			while (last - first < 5_000_000 && wall.ElapsedMilliseconds < 2000)
			{
				long now = ImportAddDiag.ThreadCpuNs();
				monotone &= now >= last;
				last = now;
			}
			double cpuMs = (last - first) / 1e6, wallMs = wall.Elapsed.TotalMilliseconds;
			Check(monotone, "the thread CPU clock never goes back");
			Check(cpuMs >= 5, $"the thread CPU clock advances on a busy loop ({cpuMs:F2} ms CPU in {wallMs:F2} ms wall)");
			Check(cpuMs <= wallMs + Epsilon, $"thread CPU <= wall on a busy loop ({cpuMs:F2} ms CPU, {wallMs:F2} ms wall)");
			Check(slice.CpuValid && slice.CpuNs > 0, "the timed Add has CPU time");
			Check(slice.CpuNs / 1e6 <= total + Epsilon && slice.EventCpuNs / 1e6 <= ev + Epsilon && slice.PhysicsCpuNs / 1e6 <= physics + Epsilon,
				$"CPU <= wall per part (add {slice.CpuNs / 1e6:F2}/{total:F2} event {slice.EventCpuNs / 1e6:F2}/{ev:F2} physics {slice.PhysicsCpuNs / 1e6:F2}/{physics:F2})");
			Check(slice.EventCpuNs > 0 && slice.PhysicsCpuNs > 0, "event and physics parts have CPU time");
			Match blocked = Regex.Match(fields, @" addCpuMs=([\d.]+) addBlockedMs=(-?[\d.]+) ");
			Check(blocked.Success && double.Parse(blocked.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) >= -Epsilon, "addBlockedMs = total - CPU: " + fields);
		}

		// Known positive for addLockContention: the Add waits for a lock another thread holds.
		ImportAddSlice contended = new ImportAddSlice();
		object gate = new object();
		using (System.Threading.ManualResetEventSlim held = new System.Threading.ManualResetEventSlim())
		{
			System.Threading.Thread holder = new System.Threading.Thread(() => { lock (gate) { held.Set(); System.Threading.Thread.Sleep(50); } }) { IsBackground = true };
			holder.Start();
			held.Wait();
			ImportAddDiag.Run(imported, contended, () => { lock (gate) { } });
			holder.Join();
		}
#if NETCOREAPP3_0_OR_GREATER
		Check(contended.LockContention >= 1, "a lock wait inside the Add is counted (addLockContention " + contended.LockContention + ")");
		if (contended.CpuValid && ImportAddDiag.ThreadCpuNs() >= 0)
			Check(Ms(contended.AddTicks) - contended.CpuNs / 1e6 >= 20, $"a lock wait inside the Add is blocked time (wall {Ms(contended.AddTicks):F2} CPU {contended.CpuNs / 1e6:F2})");
#endif

		// The -1 path: with the clock unavailable (this thread only) the CPU fields print -1 and nothing throws.
		ImportAddSlice noClock = new ImportAddSlice();
		ImportAddDiag.t_cpuClockOffForTest = true;
		try
		{
			ImportAddDiag.Run(imported, noClock, () => { ImportAddDiag.EventEnter(); ImportAddDiag.EventExit(); ImportAddDiag.PhysicsEnter(); ImportAddDiag.PhysicsExit(); });
		}
		finally
		{
			ImportAddDiag.t_cpuClockOffForTest = false;
		}
		string noClockFields = noClock.Fields();
		Check(!noClock.CpuValid && noClockFields.Contains(" addCpuMs=-1 addBlockedMs=-1 ") && noClockFields.Contains(" addEventCpuMs=-1 addPhysicsCpuMs=-1 "),
			"no CPU clock prints -1: " + noClockFields);
		Check(!linux || ImportAddDiag.ThreadCpuNs() >= 0, "the test's -1 path did not disable the clock for the node");

		// Release side (source node): RunRelease times the close sequence as N entities; only the release hooks count
		// into a release slice, only the add hooks into an add slice; no import mark; fields in order; a throw clears it.
		ImportAddSlice release = new ImportAddSlice { Release = true };
		bool markedInRelease = true;
		ImportAddDiag.RunRelease(release, 3, () =>
		{
			markedInRelease = ImportAddDiag.InImportAdd;
			ImportAddDiag.EventEnter(); Spin(1); ImportAddDiag.EventExit();          // add-side hooks: not counted here
			ImportAddDiag.PhysicsEnter(); Spin(1); ImportAddDiag.PhysicsExit();
			ImportAddDiag.CountClusterCreate();
			ImportAddDiag.PhysicsEnter(release: true); ImportAddDiag.PhysicsEnter(release: true); Spin(2); ImportAddDiag.PhysicsExit(release: true); ImportAddDiag.PhysicsExit(release: true);
			ImportAddDiag.EventEnter(release: true); Spin(2); ImportAddDiag.PhysicsEnter(release: true); Spin(1); ImportAddDiag.PhysicsExit(release: true); ImportAddDiag.EventExit(release: true);
			Spin(1);
		});
		Check(!markedInRelease, "a release is not an import Add (GridAddTrace unaffected)");
		Check(release.Adds == 3 && release.EventCalls == 1 && release.PhysicsCalls == 1 && release.ClusterCreates == 0,
			$"release: relN is the entity count, only release hooks count, outermost only (n {release.Adds} events {release.EventCalls} physics {release.PhysicsCalls} creates {release.ClusterCreates})");
		double relTotal = Ms(release.AddTicks), relEvent = Ms(release.EventTicks), relPhysics = Ms(release.PhysicsTicks);
		Check(relPhysics >= 2 && relEvent >= 3 && relTotal >= 8 && relEvent + relPhysics <= relTotal,
			$"release times cover the spins and stay disjoint (total {relTotal:F2} event {relEvent:F2} physics {relPhysics:F2})");
		ImportAddSlice addOnly = new ImportAddSlice();
		ImportAddDiag.Run(imported, addOnly, () => { ImportAddDiag.EventEnter(release: true); ImportAddDiag.EventExit(release: true); ImportAddDiag.PhysicsEnter(release: true); ImportAddDiag.PhysicsExit(release: true); });
		Check(addOnly.EventCalls == 0 && addOnly.PhysicsCalls == 0, "release hooks do not count into an import Add");
		string relFields = release.ReleaseFields();
		string relKeys = string.Join(" ", Regex.Matches(relFields, @" (\w+)=").Select(m => m.Groups[1].Value));
		Check(relKeys == "relN relTotalMs relCpuMs relBlockedMs relLockContention relEventMs relEventCpuMs relPhysicsMs relPhysicsCpuMs relOtherMs"
			+ " relGrids relBlocksSum relGc0 relGc1 relGc2 relGcPauseMs relOthers relBlocksMax relFatBlocksSum relEventCalls relPhysicsCalls"
			+ " relGridBeforeDeleteMs relSceneRemoveMs relSystemsMs relOnCloseMs relUnsubscribeMs relReplDestroyMs relReplForClientsMs"
			+ " relReplStateGroupsMs relReplNetObjMs relUnsubscribeN relReplDestroyN relReplForClientsN relReplStateGroupsN relReplNetObjN",
			"release field order: " + relFields);
		// T-0404 release sub-timers: outermost per kind, nested kinds overlap (inclusive), inert in an import Add and untimed.
		ImportAddSlice relSub = new ImportAddSlice { Release = true };
		ImportAddDiag.RunRelease(relSub, 1, () =>
		{
			ImportAddDiag.RelSubEnter(0); ImportAddDiag.RelSubEnter(0); Spin(2); ImportAddDiag.RelSubEnter(1); Spin(2); ImportAddDiag.RelSubExit(1);
			ImportAddDiag.RelSubExit(0); ImportAddDiag.RelSubExit(0); Spin(1);
		});
		ImportAddDiag.RelSubEnter(2); Spin(1); ImportAddDiag.RelSubExit(2);
		// T-0404 release look: counted kinds count their outermost calls only, and only inside a timed release.
		ImportAddSlice relCount = new ImportAddSlice { Release = true };
		ImportAddDiag.RunRelease(relCount, 1, () =>
		{
			ImportAddDiag.RelSubEnter(5); ImportAddDiag.RelSubEnter(8); ImportAddDiag.RelSubExit(8); ImportAddDiag.RelSubEnter(8); ImportAddDiag.RelSubExit(8);
			ImportAddDiag.RelSubEnter(5); ImportAddDiag.RelSubExit(5); ImportAddDiag.RelSubExit(5);
		});
		ImportAddDiag.RelSubEnter(4); ImportAddDiag.RelSubExit(4);
		Check(relCount.RelSubCalls[5] == 1 && relCount.RelSubCalls[8] == 2 && relCount.RelSubCalls[4] == 0
			&& relCount.ReleaseFields().Contains(" relUnsubscribeN=0 relReplDestroyN=1 relReplForClientsN=0 relReplStateGroupsN=0 relReplNetObjN=2"),
			"release sub-timer counts: outermost per kind, inert outside a timed release: " + relCount.ReleaseFields());
		ImportAddSlice relSubAdd = new ImportAddSlice();
		ImportAddDiag.Run(imported, relSubAdd, () => { ImportAddDiag.RelSubEnter(3); Spin(1); ImportAddDiag.RelSubExit(3); });
		double sub0 = Ms(relSub.RelSubTicks[0]), sub1 = Ms(relSub.RelSubTicks[1]);
		Check(sub0 >= 4 && sub1 >= 2 && sub1 <= sub0 && sub0 <= Ms(relSub.AddTicks) && relSub.RelSubTicks[2] == 0 && relSub.RelSubTicks[3] == 0
			&& relSubAdd.RelSubTicks[3] == 0, $"release sub-timers: outermost, inclusive, inert outside a timed release (beforeDelete {sub0:F2} sceneRemove {sub1:F2})");
		Match relOther = Regex.Match(relFields, @" relOtherMs=(-?[\d.]+) ");
		Check(relOther.Success && double.Parse(relOther.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 0, "relOtherMs = total - event - physics: " + relFields);
		if (ImportAddDiag.ThreadCpuNs() >= 0)
			Check(release.CpuValid && release.CpuNs > 0 && release.CpuNs / 1e6 <= relTotal + Epsilon && release.EventCpuNs / 1e6 <= relEvent + Epsilon
				&& release.PhysicsCpuNs / 1e6 <= relPhysics + Epsilon, "release CPU <= wall per part: " + relFields);
		ImportAddSlice relThrown = new ImportAddSlice { Release = true };
		try { ImportAddDiag.RunRelease(relThrown, 1, () => { ImportAddDiag.EventEnter(release: true); throw new InvalidOperationException("close"); }); }
		catch (InvalidOperationException e) when (e.Message == "close") { }
		ImportAddDiag.EventEnter(release: true); ImportAddDiag.EventExit(release: true);
		Check(relThrown.Adds == 1 && relThrown.EventCalls == 0, "a throwing close is counted, nothing counts into it afterwards");
		ImportAddSlice relNoClock = new ImportAddSlice { Release = true };
		ImportAddDiag.t_cpuClockOffForTest = true;
		try { ImportAddDiag.RunRelease(relNoClock, 1, () => { }); }
		finally { ImportAddDiag.t_cpuClockOffForTest = false; }
		Check(relNoClock.ReleaseFields().Contains(" relCpuMs=-1 relBlockedMs=-1 ") && relNoClock.ReleaseFields().Contains(" relEventCpuMs=-1 ")
			&& relNoClock.ReleaseFields().Contains(" relPhysicsCpuMs=-1 "), "release without a CPU clock prints -1: " + relNoClock.ReleaseFields());

		// Node-age tags: a private counter (the node's own Imports/Releases counters are not bumped by a self-test).
		NodeAgeCounter counter = new NodeAgeCounter("importIndex");
		int firstIndex = counter.Next(), secondIndex = counter.Next();
		Check(firstIndex == 1 && secondIndex == 2, $"importIndex is 1-based and increments per import ({firstIndex}, {secondIndex})");
		Match tags = Regex.Match(counter.Fields(secondIndex), @"^ nodeAgeS=(\d+\.\d) importIndex=2$");
		Check(tags.Success && double.Parse(tags.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 0, "node-age fields: " + counter.Fields(secondIndex));
		Check(NodeAgeCounter.Imports.Fields(7).EndsWith(" importIndex=7") && NodeAgeCounter.Releases.Fields(3).EndsWith(" releaseIndex=3"),
			"the import and release lines carry importIndex / releaseIndex");
	}
}
