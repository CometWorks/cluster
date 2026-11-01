// #define LIST_ALL_TYPES

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Shared.Logging;
using Shared.Plugin;
using Shared.Tools;

namespace Shared.Patches;

// ReSharper disable once UnusedType.Global
public static class PatchHelpers
{
    public static bool HarmonyPatchAll(IPluginLogger log, Harmony harmony)
    {
#if DEBUG && LIST_ALL_TYPES
            log.Info("All types:");
            foreach (var typ in AccessTools.AllTypes())
            {
                log.Info(typ.FullName);
            }
#endif

        // T-0122: report-only hash dump, off unless CLUSTER_PATCH_HASHES=1. The hash EnsureCode
        // checks is a RUNTIME quantity (PatchProcessor.GetCurrentInstructions reads the method as
        // currently loaded), so it cannot be precomputed offline and trusted. Declaring a guessed
        // or stale hash makes the plugin REFUSE TO LOAD, i.e. stops the node - so the safe order is
        // to run a node with this on, take the real hashes from its log, and only then declare
        // them. Emitted BEFORE any patch is applied, so the hashes are of the code as the game
        // ships it (plus any other plugin's patches, which is the point - EnsureCode detects those
        // too, not only game updates).
        if (Environment.GetEnvironmentVariable("CLUSTER_PATCH_HASHES") == "1")
        {
            log.Info("CLUSTER_PATCH_HASHES=1: reporting current body hashes of all patch targets");
            try
            {
                var reported = 0;
                foreach (var line in EnsureCode.ReportHashes(Assembly.GetExecutingAssembly()))
                {
                    log.Info(line);
                    reported++;
                }
                log.Info("EnsureCode-hash: {0} patch method(s) reported", reported);
            }
            catch (Exception ex)
            {
                // Never fatal: this is a diagnostic and must not be able to stop a node.
                log.Error(ex, "Failed to report patch target hashes");
            }
        }

        log.Debug("Scanning for conflicting code changes");
        try
        {
            var codeChanges = EnsureCode.Verify().ToList();
            if (codeChanges.Count != 0)
            {
                log.Critical("Detected conflicting code changes:");
                foreach (var codeChange in codeChanges)
                    log.Critical(codeChange.ToString());
                return false;
            }
            // T-0122: say how many were CHECKED, not just that none failed. A scan that passes
            // vacuously is worse than no scan - it puts a clean line in the log that a reader
            // takes as evidence the patches were verified. This one checked 0 of 77 from
            // 2026-09-02 until the attributes are declared, and nothing said so.
            if (EnsureCode.LastVerifiedCount == 0)
                log.Warning("No patch method declares [EnsureCode]: game-code changes are NOT being detected");
            else
                log.Info("Verified {0} patch method(s) against declared body hashes", EnsureCode.LastVerifiedCount);
        }
        catch (Exception ex)
        {
            log.Critical(ex, "Failed to scan for conflicting code changes");
            return false;
        }

        log.Debug("Applying Harmony patches");
        // Per patch class, so one failure does not hide the others: a node with a missing patch is
        // wrong either way, and a full list per run is what the port needs.
        var failures = 0;
        foreach (var type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
        {
            // Only annotated patch classes. The durable patch bodies (Shared/Cluster/Patches)
            // keep Harmony-style method names (Prefix, Postfix, Prepare) and are called by the annotated
            // classes; Harmony 2.4 would otherwise try to patch them as unannotated patch classes.
            if (!type.IsDefined(typeof(HarmonyPatch), true))
                continue;
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception ex)
            {
                failures++;
                log.Critical(ex, "Failed to apply Harmony patch class {0}", type.FullName);
            }
        }
        if (failures > 0)
        {
            log.Critical("{0} Harmony patch class(es) failed", failures);
            return false;
        }

        return true;
    }

    // Called after loading configuration, but before patching
    public static void Configure()
    {
        // Call the Configure public static method on each of your patch
        // classes requiring one-time configuration (no world is loaded yet)
        // For example:
        //MyPatch.Configure();
    }

    // Called on every update
    public static void PatchUpdates()
    {
        // Call the Update public static method on each of your
        // patch classes which needs periodic updates, it is
        // called on each game update (tick).
        // For example:
        //MyPatch.Update();
    }
}