using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// SE-1924: lets Modular Encounters Systems (MES) keep an imported NPC grid's encounter identity.
/// <para>
/// MES builds its wrapper for every grid it sees added. It trusts the NPC record in the grid's storage only
/// for grids in <c>EntityWatcher.GridsOnLoad</c>, the grids present when its session started. Any other grid
/// must match a spawn MES started in this process. A handover import matches neither, so MES drops the
/// record, its new-grid task writes a blank one a second later, and the encounter forgets its spawn group,
/// prefab, origin and spawned-by-MES flag (SE-1912).
/// </para>
/// <para>
/// An import is a load of a saved grid, so right before the import Adds a grid that carries an MES record
/// this puts the grid into that list. MES then takes its own world-load path: it reads the record, registers
/// the grid as an active NPC, and fires no spawn callbacks and charges no spawn costs. Grids without a record,
/// and worlds without MES, are untouched. The entry is removed when the grid closes.
/// </para>
/// </summary>
internal static class MesAdoption
{
    private const string MesNamespace = "ModularEncountersSystems";
    private const string WatcherType = MesNamespace + ".Entities.EntityWatcher";

    // MES StorageTools.NpcDataKey and LegacyNpcDataKey
    private static readonly Guid NpcDataKey = new("29FFD684-13D7-4045-BF76-CD48BF80E36A");
    private static readonly Guid LegacyNpcDataKey = new("AD4DBD09-359D-48F5-9F48-54D352B59171");

    private static MyScriptManager m_scripts;
    private static List<IMyCubeGrid> m_gridsOnLoad;

    /// <summary>Game thread, right before MyEntities.Add of an imported entity.</summary>
    public static void BeforeAdd(MyEntity entity)
    {
        if (
            entity is not MyCubeGrid grid
            || grid.Storage == null
            || !(grid.Storage.ContainsKey(NpcDataKey) || grid.Storage.ContainsKey(LegacyNpcDataKey))
        )
            return;
        List<IMyCubeGrid> list = GridsOnLoad();
        if (list == null || list.Contains(grid))
            return;
        list.Add(grid);
        ((VRage.ModAPI.IMyEntity)grid).OnClose += _ => list.Remove(grid);
    }

    /// <summary>
    /// The loaded MES's GridsOnLoad, or null. Looked up once per session. When MES is loaded but the list is
    /// gone (renamed, retyped), this logs an error and returns null: imports then run as before the fix, and the
    /// encounters lose their identity on handover, but nothing breaks.
    /// </summary>
    private static List<IMyCubeGrid> GridsOnLoad()
    {
        MyScriptManager scripts = MySession.Static?.ScriptManager;
        if (scripts == m_scripts)
            return m_gridsOnLoad;
        m_scripts = scripts;
        m_gridsOnLoad = null;
        string status;
        try
        {
            Assembly[] mes = (scripts?.Scripts.Values ?? Enumerable.Empty<Assembly>())
                .Where(assembly =>
                    assembly.GetType(WatcherType, false) != null
                    || TypesOf(assembly)
                        .Any(type => type?.Namespace?.StartsWith(MesNamespace) == true)
                )
                .ToArray();
            m_gridsOnLoad = mes.Select(assembly =>
                    assembly
                        .GetType(WatcherType, false)
                        ?.GetField(
                            "GridsOnLoad",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                        )
                        ?.GetValue(null) as List<IMyCubeGrid>
                )
                .FirstOrDefault(list => list != null);
            status =
                m_gridsOnLoad != null ? "enabled"
                : mes.Length == 0 ? "no MES loaded"
                : "ERROR MES is loaded but " + WatcherType + ".GridsOnLoad is missing";
        }
        catch (Exception error)
        {
            status = "ERROR lookup failed: " + error.GetType().Name + ": " + error.Message;
        }
        if (status.StartsWith("ERROR"))
            status += "; NPC encounters lose their MES identity on handover (SE-1912)";
        MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] mes_adoption " + status);
        return m_gridsOnLoad;
    }

    private static Type[] TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            return partial.Types;
        }
    }
}
