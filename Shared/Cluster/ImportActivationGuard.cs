using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// T-0392: the entities of a staged import whose SLICED activation (import.activatePerFrame=N&gt;0) is
/// open on this node, from before the first import_activate_slice frame to the end of the
/// import_activate_final frame, whatever way that ends (final, bail, discard, exception).
///
/// WHY. A sliced activation Adds the partition over several frames and collects the round-trip
/// builders only in the final frame. Between those frames the game thread runs every periodic
/// service in ClusterRuntime.Update; one that WRITES an imported entity changes what the final frame
/// collects, and the round-trip gate aborts the handover on a difference the source never had.
/// Live 2026-10-05 00:22:56 (transfer 081ae0b3, 140 entities, 8 slices): ShipAntennaService switched
/// off the radio antennas of 7 'Visitor Ion' ships from an early slice (unpiloted until commit, frozen
/// so speed 0) -> residuals=7 'CubeBlock/Enabled: true != false' -> abort. With N=0 activation and
/// collection are one frame, so nothing can run between them; this guard restores that property for
/// the services that write the world: each skips an entity for which <see cref="IsOpen(long)"/> holds.
///
/// One generic predicate rather than per-service import awareness: a service asks "is this entity
/// still being activated?" by entity id (a grid, a character, or the top-most parent of a block).
/// The ids are the import's top-level builder ids (staged AND kept: the final frame collects both).
///
/// Threading: opened and released on the handover handler thread, read on the game thread. Reads are
/// lock-free (ContainsKey on a ConcurrentDictionary, and a zero-count fast path - the predicate sits
/// in per-grid loops); writes take a lock so overlapping guards keep an exact per-id count.
/// </summary>
internal static class ImportActivationGuard
{
	private static readonly ConcurrentDictionary<long, int> s_open = new ConcurrentDictionary<long, int>();
	private static readonly object s_write = new object();
	private static int s_scopes;
	private static long s_opened, s_released, s_guardedImports, s_guardedIds;
	private static readonly long[] s_skips = new long[Enum.GetValues(typeof(GuardUser)).Length];

	/// <summary>True while a sliced activation that carries <paramref name="entityId"/> is open on this node.</summary>
	internal static bool IsOpen(long entityId) => Volatile.Read(ref s_scopes) != 0 && s_open.ContainsKey(entityId);

	/// <summary>The entity, or its top-most parent (a block's grid, a seated character's grid), is guarded.</summary>
	internal static bool IsOpen(MyEntity entity)
	{
		if (entity == null || Volatile.Read(ref s_scopes) == 0) return false;
		if (s_open.ContainsKey(entity.EntityId)) return true;
		MyEntity top = entity.GetTopMostParent();
		return top != null && top != entity && s_open.ContainsKey(top.EntityId);
	}

	/// <summary>
	/// <see cref="IsOpen(long)"/> for a caller that SKIPS its work when it holds, counting each skip per
	/// caller for GET /cluster/import-guard (diagnostic only). Same boolean as IsOpen and the same
	/// zero-cost fast path while nothing is open; the counter moves only on a true answer.
	/// </summary>
	internal static bool IsOpenFor(GuardUser user, long entityId)
	{
		if (!IsOpen(entityId)) return false;
		Interlocked.Increment(ref s_skips[(int)user]);
		return true;
	}

	/// <summary><see cref="IsOpen(MyEntity)"/>, counted per caller like <see cref="IsOpenFor(GuardUser, long)"/>.</summary>
	internal static bool IsOpenFor(GuardUser user, MyEntity entity)
	{
		if (!IsOpen(entity)) return false;
		Interlocked.Increment(ref s_skips[(int)user]);
		return true;
	}

	/// <summary>Skips counted for <paramref name="user"/> since start.</summary>
	internal static long Skips(GuardUser user) => Interlocked.Read(ref s_skips[(int)user]);

	/// <summary>
	/// Self-test only: takes back the counts a self-test produced against its own fake ids, so a live
	/// node's /cluster/import-guard keeps reporting real imports and real skips.
	/// </summary>
	internal static void Discount(long imports, long ids)
	{
		Interlocked.Add(ref s_guardedImports, -imports);
		Interlocked.Add(ref s_guardedIds, -ids);
	}

	/// <inheritdoc cref="Discount(long, long)"/>
	internal static void Discount(GuardUser user, long skips) => Interlocked.Add(ref s_skips[(int)user], -skips);

	/// <summary>Guarded sliced imports (RunSliced entries) and the ids they guarded, since start.</summary>
	internal static (long Imports, long Ids) Guarded => (Interlocked.Read(ref s_guardedImports), Interlocked.Read(ref s_guardedIds));

	/// <summary>Open guards, for /diag and the self-test.</summary>
	internal static int OpenScopes => Volatile.Read(ref s_scopes);

	/// <summary>GET /cluster/import-guard: open now, guarded sliced imports and ids since start, skips per caller.</summary>
	internal static object Status()
	{
		Dictionary<string, long> skips = new Dictionary<string, long>();
		foreach (GuardUser user in (GuardUser[])Enum.GetValues(typeof(GuardUser))) skips[user.ToString()] = Skips(user);
		return new
		{
			openNow = OpenScopes,
			openIds = s_open.Count,
			guardedImports = Guarded.Imports,
			guardedIds = Guarded.Ids,
			opened = Interlocked.Read(ref s_opened),
			released = Interlocked.Read(ref s_released),
			skips
		};
	}

	/// <summary>Guards <paramref name="entityIds"/> until the returned scope is disposed (idempotent).</summary>
	internal static Scope Open(IEnumerable<long> entityIds)
	{
		List<long> ids = new List<long>();
		lock (s_write)
		{
			foreach (long id in entityIds)
			{
				if (id == 0) continue;
				ids.Add(id);
				s_open.AddOrUpdate(id, 1, (_, count) => count + 1);
			}
			s_scopes++;
		}
		Interlocked.Increment(ref s_opened);
		return new Scope(ids);
	}

	/// <summary>
	/// The sliced activation driver: guards <paramref name="entityIds"/>, runs <paramref name="slice"/>
	/// until it returns false (no slice left), then <paramref name="final"/>, and releases the guard on
	/// every way out - the final frame's return, a slice or the final frame throwing (bail, discard,
	/// game-thread timeout). <paramref name="slices"/> receives the number of slice frames run.
	/// </summary>
	internal static T RunSliced<T>(IEnumerable<long> entityIds, Func<bool> slice, Func<T> final, out int slices)
	{
		slices = 0;
		using Scope scope = Open(entityIds);
		Interlocked.Increment(ref s_guardedImports);
		Interlocked.Add(ref s_guardedIds, scope.Count);
		bool more;
		do
		{
			more = slice();
			slices++;
		}
		while (more);
		return final();
	}

	internal sealed class Scope : IDisposable
	{
		private List<long> m_ids;

		internal Scope(List<long> ids) => m_ids = ids;

		/// <summary>The non-zero ids this scope guards (0 once released).</summary>
		internal int Count => Volatile.Read(ref m_ids)?.Count ?? 0;

		public void Dispose()
		{
			List<long> ids = Interlocked.Exchange(ref m_ids, null);
			if (ids == null) return;
			lock (s_write)
			{
				foreach (long id in ids)
				{
					if (!s_open.TryGetValue(id, out int count)) continue;
					if (count <= 1) s_open.TryRemove(id, out _);
					else s_open[id] = count - 1;
				}
				s_scopes--;
			}
			Interlocked.Increment(ref s_released);
		}
	}
}

/// <summary>The callers that skip work while <see cref="ImportActivationGuard"/> is open, for the per-caller skip counters.</summary>
internal enum GuardUser
{
	/// <summary>ShipAntennaService reconcile (a guarded grid; frozen skips are not counted here).</summary>
	ShipAntenna,
	/// <summary>CharacterRegistryService first-seen of a body.</summary>
	CharacterSeed,
	/// <summary>CharacterRegistryService settle of a body's helmet/light/broadcasting.</summary>
	CharacterSettle,
	/// <summary>PartitionPersistence fenced-orphan purge: a tainted id treated as re-acquired.</summary>
	FencePurge,
	/// <summary>PartitionPersistence recovery: a fence-tainted stale copy not replaced.</summary>
	RecoveryStaleCopy,
	/// <summary>MyTimerComponentSystemPatch: a session timer tick withheld (per timer per tick).</summary>
	Timer,
	/// <summary>MySessionComponentSmartUpdaterPatch: a grid presence update withheld.</summary>
	SmartUpdater,
	/// <summary>ShipStorageService sweep of grids that are stored ships (SE1-0233).</summary>
	ShipStorage,
	/// <summary>UnvisitedNpcSweeper (a guarded grid is not removed this pass, #1354).</summary>
	NpcSweep,
	/// <summary>WreckageCleaner (a guarded grid is not removed this pass, #1390).</summary>
	WreckCleanup,
	/// <summary>BotShipCleaner (a guarded grid is not removed this run, SE-1316).</summary>
	BotShipCleanup,
}
