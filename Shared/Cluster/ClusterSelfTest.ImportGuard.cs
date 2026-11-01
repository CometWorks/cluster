using System;
using System.Linq;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// T-0392: the sliced-activation guard. While a sliced staged import is between its first
	/// import_activate_slice and the end of import_activate_final, the world-writing periodic services must
	/// leave its entities alone (live 2026-10-05 00:22:56: ShipAntennaService switched 7 antennas off between
	/// slices, residuals=7, abort). Checks the guard's lifetime through the SAME driver the import uses
	/// (ImportActivationGuard.RunSliced): open inside every slice and the final frame, released after the
	/// final, after a slice that bails (throws), after a final that throws; overlapping guards count per id;
	/// and ShipAntennaService's skip decision follows the guard. No game types: entity ids only (ids far from
	/// any real entity id). The diagnostic counters (GET /cluster/import-guard): RunSliced counts the import
	/// and its ids; IsOpenFor answers as IsOpen and counts a skip only when it holds, only for the asking
	/// caller; ShipAntennaService tells a guard skip from a frozen one (a fake id is frozen for that, briefly).
	/// What this section counts against its fake ids it takes back at the end, so a live node's counters stay
	/// real. Like the scope-count check, it assumes no real sliced import opens while it runs.
	private static void ImportActivationGuardRules()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("import-activation-guard: " + message); }
		long[] ids = { 7_392_000_001, 7_392_000_002, 7_392_000_003 };
		const long Other = 7_392_000_099;
		int scopesBefore = ImportActivationGuard.OpenScopes;
		(long importsBefore, long idsBefore) = ImportActivationGuard.Guarded;
		bool AnyOpen() => ids.Any(ImportActivationGuard.IsOpen);
		bool AllOpen() => ids.All(ImportActivationGuard.IsOpen);

		Check(!AnyOpen() && !ImportActivationGuard.IsOpen(Other), "nothing guarded before the first slice");
		Check(!ids.Any(ShipAntennaService.Skips), "the antenna reconcile does not skip an unguarded grid");

		// Success path: 3 slice frames, then the final frame.
		int calls = 0;
		bool openInEverySlice = true, openInFinal = false, antennaSkipsInSlice = true;
		string result = ImportActivationGuard.RunSliced(ids,
			() =>
			{
				calls++;
				openInEverySlice &= AllOpen() && !ImportActivationGuard.IsOpen(Other);
				antennaSkipsInSlice &= ids.All(ShipAntennaService.Skips) && !ShipAntennaService.Skips(Other);
				return calls < 3;
			},
			() => { openInFinal = AllOpen(); return "compared"; },
			out int slices);
		Check(result == "compared" && slices == 3 && calls == 3, $"the driver runs every slice then the final (slices={slices}, calls={calls}, result={result})");
		Check(openInEverySlice, "guarded in every slice frame (and only the import's ids)");
		Check(antennaSkipsInSlice, "ShipAntennaService skips a grid whose sliced activation is open");
		Check(openInFinal, "still guarded inside the final frame (where the round-trip builders are collected)");
		Check(!AnyOpen(), "released after the final frame");
		Check(ImportActivationGuard.Guarded == (importsBefore + 1, idsBefore + ids.Length),
			$"RunSliced counts one guarded import and its {ids.Length} ids (is {ImportActivationGuard.Guarded}, was {(importsBefore, idsBefore)})");
		Check(!ids.Any(ShipAntennaService.Skips), "the antenna reconcile resumes after the final frame");

		// A slice bails (ThrowIfAbandoned: import failed or transfer discarded between slices).
		calls = 0;
		bool finalRan = false;
		try
		{
			ImportActivationGuard.RunSliced(ids,
				() => { if (++calls == 2) throw new InvalidOperationException("Transfer was discarded between activation slices"); return true; },
				() => { finalRan = true; return 0; }, out _);
			Check(false, "a bailing slice must propagate its exception");
		}
		catch (InvalidOperationException e) when (e.Message.StartsWith("Transfer was discarded", StringComparison.Ordinal)) { }
		Check(calls == 2 && !finalRan, "a bail stops the slices and skips the final frame");
		Check(!AnyOpen(), "released after a slice bails");

		// The final frame throws (round-trip collection failure, game-thread timeout).
		try
		{
			ImportActivationGuard.RunSliced<int>(ids, () => false, () => { Check(AllOpen(), "guarded in a final frame that then throws"); throw new TimeoutException("final"); }, out _);
			Check(false, "a throwing final frame must propagate its exception");
		}
		catch (TimeoutException) { }
		Check(!AnyOpen(), "released after the final frame throws");

		// Overlapping guards (two imports naming one id) count per id; release is idempotent.
		ImportActivationGuard.Scope first = ImportActivationGuard.Open(new[] { ids[0], ids[1] });
		ImportActivationGuard.Scope second = ImportActivationGuard.Open(new[] { ids[1], ids[2] });
		Check(AllOpen(), "two open guards cover the union");
		first.Dispose();
		first.Dispose();
		Check(!ImportActivationGuard.IsOpen(ids[0]) && ImportActivationGuard.IsOpen(ids[1]) && ImportActivationGuard.IsOpen(ids[2]),
			"releasing one guard (twice) keeps the ids the other still holds");
		second.Dispose();
		Check(!AnyOpen(), "released after both guards");
		Check(ImportActivationGuard.OpenScopes == scopesBefore, $"scope count back to {scopesBefore} (is {ImportActivationGuard.OpenScopes})");
		Check(ImportActivationGuard.Guarded == (importsBefore + 3, idsBefore + 3 * ids.Length),
			"a bailed and a throwing RunSliced count as guarded imports too; Open alone does not");
		ImportActivationGuard.Discount(3, 3 * ids.Length);

		// Per-caller skip counters: the same boolean as IsOpen, a count only on true, only for the caller.
		long Skips(GuardUser user) => ImportActivationGuard.Skips(user);
		long timer = Skips(GuardUser.Timer), purge = Skips(GuardUser.FencePurge), antenna = Skips(GuardUser.ShipAntenna);
		Check(!ImportActivationGuard.IsOpenFor(GuardUser.Timer, ids[0]) && Skips(GuardUser.Timer) == timer,
			"IsOpenFor is false and counts nothing while no guard is open");
		using (ImportActivationGuard.Open(new[] { ids[0] }))
		{
			Check(ImportActivationGuard.IsOpenFor(GuardUser.Timer, ids[0]) && ImportActivationGuard.IsOpenFor(GuardUser.Timer, ids[0]),
				"IsOpenFor is true for a guarded id");
			Check(!ImportActivationGuard.IsOpenFor(GuardUser.Timer, Other), "IsOpenFor is false for an unguarded id while a guard is open");
			Check(Skips(GuardUser.Timer) == timer + 2, $"each true answer counts once, the false one not (Timer +{Skips(GuardUser.Timer) - timer}, want +2)");
			Check(Skips(GuardUser.FencePurge) == purge, "a skip counts only for the asking caller");
			Check(ImportActivationGuard.IsOpenFor(GuardUser.FencePurge, ids[0]) && Skips(GuardUser.FencePurge) == purge + 1 && Skips(GuardUser.Timer) == timer + 2,
				"a second caller counts on its own counter");
			Check(ShipAntennaService.Skips(ids[0], out bool byGuard) && byGuard && Skips(GuardUser.ShipAntenna) == antenna + 1,
				"ShipAntennaService counts a guarded grid as a guard skip");
			Guid transfer = Guid.NewGuid();
			ClusterRuntime.MarkFrozen(Other, transfer);
			try
			{
				Check(ShipAntennaService.Skips(Other, out bool frozenByGuard) && !frozenByGuard && Skips(GuardUser.ShipAntenna) == antenna + 1,
					"ShipAntennaService skips a frozen, unguarded grid without counting a guard skip");
			}
			finally { ClusterRuntime.ClearFrozen(Other, transfer); }
		}
		Check(!ImportActivationGuard.IsOpenFor(GuardUser.Timer, ids[0]) && Skips(GuardUser.Timer) == timer + 2, "after release: false, not counted");
		ImportActivationGuard.Discount(GuardUser.Timer, 2);
		ImportActivationGuard.Discount(GuardUser.FencePurge, 1);
		ImportActivationGuard.Discount(GuardUser.ShipAntenna, 1);
	}
}
