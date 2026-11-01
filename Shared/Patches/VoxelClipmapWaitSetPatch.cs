using System;
using HarmonyLib;
using Sandbox.Game.EntityComponents.Renders;
using Sandbox.Game.World;
using VRage.Game.Components;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// T-0274 interim backstop for a vanilla leak: every voxel map that ever entered the scene stays
/// reachable. MyRenderComponentVoxelMap.AddRenderObjects puts the clipmap into the static set
/// VoxelLoadingWaitStep.Clipmaps, and only the clipmap's Loaded event takes it out. A node renders
/// nothing, so Loaded never fires, and the set held all 11,031 voxel maps node-3 had created, 84% of
/// them closed procedural asteroids, with their clipmap cell structures (two dumps, 2026-09-29).
/// The real fix is the Bugfixes plugin's VoxelClipmapWaitSetPatch (vanilla scope). This copy
/// stands in on the nodes until that release is pinned, and it is a no-op once Bugfixes has already
/// removed the clipmap. The count line is the retest oracle: on a node it should track the live
/// voxel maps, not every map ever created.
/// </summary>
[HarmonyPatch(typeof(MyRenderComponentBase), nameof(MyRenderComponentBase.RemoveRenderObjects))]
public static class VoxelClipmapWaitSetPatch
{
	private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(5);
	private static DateTime s_nextReport = DateTime.UtcNow + ReportInterval;
	private static long s_removed;

	[HarmonyPostfix]
	private static void Postfix(MyRenderComponentBase __instance)
	{
		if (!(__instance is MyRenderComponentVoxelMap voxelRender)) return;
		var clipmap = voxelRender.Clipmap;
		if (clipmap == null) return;

		var waiting = MyRenderComponentVoxelMap.VoxelLoadingWaitStep.Clipmaps;
		bool present;
		lock (waiting) present = waiting.Contains(clipmap);
		if (!present) return;

		// The game's own removal: unhooks Loaded and signals ClipmapsReady if the set empties.
		MyRenderComponentVoxelMap.VoxelLoadingWaitStep.RemoveClipmap(clipmap);
		s_removed++;
	}

	/// <summary>Game thread, from ClusterRuntime.Update: one count line every 5 minutes.</summary>
	public static void Update()
	{
		DateTime now = DateTime.UtcNow;
		if (now < s_nextReport) return;
		s_nextReport = now + ReportInterval;

		var waiting = MyRenderComponentVoxelMap.VoxelLoadingWaitStep.Clipmaps;
		int count;
		lock (waiting) count = waiting.Count;
		var voxelMaps = MySession.Static?.VoxelMaps;
		int live = voxelMaps != null ? voxelMaps.Instances.Count : -1;
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] voxel_clipmap_waitset count=" + count + " live_voxel_maps=" + live
			+ " removed_by_backstop=" + s_removed);
	}
}
