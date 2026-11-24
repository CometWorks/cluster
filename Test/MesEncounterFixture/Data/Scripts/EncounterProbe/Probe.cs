using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace ClusterMesEncounterProbe
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Probe : MySessionComponentBase
    {
        private const long MesChannel = 1521905890;
        private const string Marker = "ClusterProbe encounter marker";
        private Func<List<string>, MatrixD, Vector3, bool, string, string, bool> spawn;
        private Action<Action<IMyCubeGrid>, bool> subscribe;
        private int ticks;
        private bool before,
            requested,
            after;

        public override void LoadData()
        {
            MyAPIGateway.Utilities.RegisterMessageHandler(MesChannel, ReceiveApi);
        }

        private void ReceiveApi(object message)
        {
            var api = message as Dictionary<string, Delegate>;
            if (api == null || spawn != null)
                return;
            spawn =
                (Func<List<string>, MatrixD, Vector3, bool, string, string, bool>)
                    api["CustomSpawnRequest"];
            subscribe = (Action<Action<IMyCubeGrid>, bool>)api["RegisterSuccessfulSpawnAction"];
            subscribe(Spawned, true);
        }

        private static bool Marked(IMyCubeGrid grid)
        {
            var blocks = new List<IMySlimBlock>();
            grid.GetBlocks(blocks, b => (b.FatBlock as IMyTerminalBlock)?.CustomName == Marker);
            return blocks.Count > 0;
        }

        private void Spawned(IMyCubeGrid grid)
        {
            if (grid != null && Marked(grid))
                Log("callback=" + grid.EntityId);
        }

        public override void UpdateAfterSimulation()
        {
            if (++ticks % 30 != 0)
                return;
            if (!before && Command("before"))
            {
                before = true;
                Snapshot("before");
            }
            if (before && !requested && Command("spawn"))
            {
                requested = true;
                var pose = MatrixD.CreateWorld(
                    new Vector3D(120, 70, 110),
                    Vector3D.Forward,
                    Vector3D.Up
                );
                bool accepted =
                    spawn != null
                    && spawn(
                        new List<string> { "ClusterProbe-Encounter" },
                        pose,
                        Vector3.Zero,
                        true,
                        "SPRT",
                        "ClusterProbe"
                    );
                Log("accepted=" + accepted);
            }
            if (before && !after && Command("after"))
            {
                after = true;
                Snapshot("after");
            }
        }

        private bool Command(string phase)
        {
            return MyAPIGateway.Utilities.FileExistsInModLocation(
                phase + ".txt",
                ModContext.ModItem
            );
        }

        private void Snapshot(string phase)
        {
            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, e => e is IMyCubeGrid);
            var ids = new List<string>();
            foreach (var e in entities)
            {
                if (Marked((IMyCubeGrid)e))
                    ids.Add(e.EntityId.ToString());
            }
            ids.Sort();
            Log(
                phase
                    + "="
                    + (ids.Count == 0 ? "-" : string.Join(",", ids))
                    + " api="
                    + (spawn != null)
            );
        }

        private static void Log(string message)
        {
            MyLog.Default.WriteLineAndConsole("mes_encounter_probe " + message);
        }

        protected override void UnloadData()
        {
            if (subscribe != null)
                subscribe(Spawned, false);
            MyAPIGateway.Utilities.UnregisterMessageHandler(MesChannel, ReceiveApi);
        }
    }
}
