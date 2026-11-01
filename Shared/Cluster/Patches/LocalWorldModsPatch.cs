using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Networking;
using VRage.FileSystem;
using VRage.Game;
using VRage.GameServices;

namespace ClusterPlugin;

/// <summary>
/// T-0290: every cluster process loads the world's Workshop mods from its OWN pre-staged folder
/// and never downloads them.
///
/// On a dedicated server, vanilla MyWorkshop.DownloadWorldModsBlockingInternal initialises
/// Steam's game-server UGC and downloads every listed mod (and, with AutodetectDependencies,
/// every dependency) from the Workshop. On a cluster that would be six processes fetching on
/// their own, and because nodes recycle hourly, a mod updated mid-day would leave different nodes
/// on different versions. That breaks the rule of one version set, staged beforehand.
///
/// Skipping the download is enough. ModItem.GetPath() returns the Workshop item's folder only when
/// a download resolved it, and otherwise returns MyFileSystem.ModsPath/&lt;Name&gt; (= &lt;data&gt;/Mods/&lt;id&gt;.sbm),
/// which is where the Cluster Manager stages the mods. Script compilation, definitions and world storage
/// (MyModContext) all go through that path. The skipped download would also have attached the
/// Workshop item (ModItem.SetModData); a local stand-in with that folder and its size is attached instead.
///
/// A mod whose folder is missing FAILS the load (Result Fail, the same as a failed download) and names
/// the folder. It is never fetched. The dependency list is not auto-detected either, so the world's
/// mod list must name every dependency.
///
/// CLUSTER_LOCAL_WORLD_MODS=0 restores the vanilla download (the escape hatch).
/// </summary>
[HarmonyPatch(typeof(MyWorkshop), nameof(MyWorkshop.DownloadWorldModsBlockingInternal))]
internal static class LocalWorldModsPatch
{
    [HarmonyPrepare]
    private static bool Prepare() => Environment.GetEnvironmentVariable("CLUSTER_LOCAL_WORLD_MODS") != "0";

    [HarmonyPrefix]
    private static bool Prefix(List<MyObjectBuilder_Checkpoint.ModItem> mods, ref MyWorkshop.ResultData __result)
    {
        if (!Sandbox.Engine.Platform.Game.IsDedicated)
            return true;
        __result = new MyWorkshop.ResultData(MyGameServiceCallResult.OK, false);
        if (mods == null || mods.Count == 0)
            return false;
        var missing = new List<string>();
        for (int i = 0; i < mods.Count; i++)
        {
            // ModItem is a struct: work on a copy and write it back, or SetModData is lost
            MyObjectBuilder_Checkpoint.ModItem mod = mods[i];
            if (mod.PublishedFileId == 0)
            {
                // vanilla refuses local mods on a DS; keep that verdict
                Log($"cluster_local_world_mods refused: local mod '{mod.Name}' (PublishedFileId 0) is not allowed in multiplayer");
                __result = default;
                return false;
            }
            string path = mod.GetPath();
            bool present = Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
            if (present)
            {
                mod.SetModData(LocalItem(mod, path));
                mods[i] = mod;
            }
            Log($"cluster_local_world_mods {(present ? "local" : "MISSING")} id={mod.PublishedFileId} name={mod.Name} path={path}");
            if (!present)
                missing.Add(path);
        }
        if (missing.Count > 0)
        {
            Log($"cluster_local_world_mods FAILED: {missing.Count} of {mods.Count} mod folder(s) missing; the load fails instead of downloading: {string.Join(", ", missing)}");
            __result = new MyWorkshop.ResultData(MyGameServiceCallResult.Fail, false);
            return false;
        }
        Log($"cluster_local_world_mods ok: {mods.Count} mod(s) from {MyFileSystem.ModsPath}, no download");
        return false;
    }

    /// <summary>The Workshop item a download would have attached. The DS reads it right after the load
    /// (MyDedicatedServerBase.Mods sums every item's Size for the server info, and throws "Mod data not
    /// initialized" without it), and MyScriptManager reads its tags. Folder = the staged path, so
    /// GetPath() is unchanged; no tags, as for a script mod the DS runs.</summary>
    private static MyWorkshopItem LocalItem(MyObjectBuilder_Checkpoint.ModItem mod, string path)
    {
        var item = new MyWorkshopItem { Id = mod.PublishedFileId, Title = mod.FriendlyName ?? mod.Name };
        AccessTools.PropertySetter(typeof(MyWorkshopItem), nameof(MyWorkshopItem.Folder)).Invoke(item, new object[] { path });
        ulong size = (ulong)new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        AccessTools.PropertySetter(typeof(MyWorkshopItem), nameof(MyWorkshopItem.Size)).Invoke(item, new object[] { size });
        return item;
    }

    private static void Log(string line) => MySandboxGame.Log.WriteLineAndConsole("[ClusterPlugin] " + line);
}
