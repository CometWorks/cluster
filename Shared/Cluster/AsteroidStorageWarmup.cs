using System;
using System.Diagnostics;
using System.Threading;
using Sandbox.Definitions;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// Loads the procedural asteroid voxel storages into MyStorageBase's cache on a background thread once a node's
/// session is ready, so the game thread never loads them itself.
///
/// The procedural generator builds each asteroid from prefab storages (MyCompositeShapes.FillSpan ->
/// CreateAsteroidStorage -> MyStorageBase.LoadFromFile), loaded and parsed lazily, once per process, into an LRU
/// cache (512 entries) - on the GAME thread, the first time a region is generated. A hot spare generates nothing
/// while idle, so the frame that first generates asteroids around its first imported partitions pays for all of
/// them: w32 2026-09-25 18:55:48.8-53.2, node-3 (spare activated for node-2's rotation) 4349 ms, 99% of the game
/// thread in MyProceduralAsteroidCellGenerator -> CreateAsteroidShape -> LoadFromFile -> MySparseOctree.ReadFrom.
/// Every rotation activates a cold spare, which is why the ~4 s freeze recurred every ~30 min.
///
/// Warming goes through the same public CreateAsteroidStorage call, so the cache key (path, no modifiers) is the
/// one the generator reads; LoadFromFile takes the cache lock and a thread-safe Lazy, so a generator that needs a
/// storage the warm-up is still loading waits for that one load instead of doing it again.
/// CLUSTER_ASTEROID_WARMUP=0 turns it off.
/// </summary>
internal static class AsteroidStorageWarmup
{
	private static int s_started;

	public static void Update()
	{
		if (Volatile.Read(ref s_started) != 0 || ClusterRuntime.IsWorldAuthority || MySession.Static?.Ready != true) return;
		if (Interlocked.Exchange(ref s_started, 1) != 0) return;
		if (Environment.GetEnvironmentVariable("CLUSTER_ASTEROID_WARMUP") == "0")
		{
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] asteroid storage warm-up disabled (CLUSTER_ASTEROID_WARMUP=0)");
			return;
		}
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] asteroid storage warm-up started (background)");
		new Thread(Run) { IsBackground = true, Name = "ClusterAsteroidWarmup", Priority = ThreadPriority.BelowNormal }.Start();
	}

	private static void Run()
	{
		var watch = Stopwatch.StartNew();
		long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		int loaded = 0, failed = 0;
		try
		{
			var definitions = MyDefinitionManager.Static;
			foreach (var list in new[]
			{
				definitions.GetVoxelMapStorageDefinitionsForProceduralPrimaryAdditions(),
				definitions.GetVoxelMapStorageDefinitionsForProceduralAdditions(),
				definitions.GetVoxelMapStorageDefinitionsForProceduralRemovals(),
			})
			{
				foreach (MyVoxelMapStorageDefinition definition in list)
				{
					if (definition == null) continue;
					try
					{
						if (MyCompositeShapes.CreateAsteroidStorage(definition) != null) loaded++;
						else failed++;
					}
					catch (Exception exception)
					{
						failed++;
						MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] asteroid storage warm-up: {definition.StorageFile} failed: {exception.GetType().Name}: {exception.Message}");
					}
				}
			}
		}
		catch (Exception exception)
		{
			// Diagnostics-grade helper: never able to take a node down; the generator still loads on demand.
			MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] asteroid storage warm-up aborted: {exception}");
		}
		MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] asteroid storage warm-up: {loaded} storage(s) cached, {failed} failed, {watch.ElapsedMilliseconds} ms (background), "
			+ $"{(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1048576.0:F0} MB allocated by the warm-up thread");
	}
}
