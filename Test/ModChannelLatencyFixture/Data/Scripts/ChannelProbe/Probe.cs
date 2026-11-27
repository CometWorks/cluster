using System;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace ModChannelLatency
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Probe : MySessionComponentBase
    {
        private const long Send = 0x434C55534D4F4453;
        private const long Receive = 0x434C55534D4F4452;
        private const string Channel = "cluster-latency-fixture";
        private const int Samples = 16;
        private readonly IMyFunctionalBlock[] blocks = new IMyFunctionalBlock[8];
        private readonly string[] snapshot = new string[8];
        private int frame,
            command,
            seq,
            events;
        private long grid,
            started,
            setter;
        private string mode,
            cachedOwner;
        private bool toggling,
            inSetter;

        public override void LoadData()
        {
            MyAPIGateway.Utilities.RegisterMessageHandler(Receive, OnMessage);
        }

        public override void UpdateAfterSimulation()
        {
            ++frame;
            if (toggling && frame % 11 == 0)
            {
                inSetter = true;
                setter = DateTime.UtcNow.Ticks;
                blocks[0].Enabled = !blocks[0].Enabled;
                inSetter = false;
                if (++events == Samples)
                    toggling = false;
            }
            if (frame % 15 != 0)
                return;
            string file = "step" + command + ".txt";
            if (!MyAPIGateway.Utilities.FileExistsInModLocation(file, ModContext.ModItem))
                return;
            string[] step;
            using (var r = MyAPIGateway.Utilities.ReadFileInModLocation(file, ModContext.ModItem))
                step = r.ReadToEnd().Trim().Split('|');
            command++;
            if (step[0] == "spawn")
                Spawn();
            else if (step[0] == "local")
                Local();
            else if (step[0] == "events")
                toggling = true;
            else
            {
                mode = step[0];
                grid = long.Parse(step[1]);
                seq = 0;
                if (mode == "resolve")
                {
                    started = DateTime.UtcNow.Ticks;
                    MyAPIGateway.Utilities.SendModMessage(
                        Send,
                        new object[] { "owner-of", Channel, grid, 1L }
                    );
                }
                else
                    Request();
            }
        }

        private void Spawn()
        {
            var builder = new MyObjectBuilder_CubeGrid
            {
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                DisplayName = "Mod channel latency",
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                PositionAndOrientation = new MyPositionAndOrientation(
                    new Vector3D(110, 140, 100),
                    Vector3.Forward,
                    Vector3.Up
                ),
            };
            for (int i = 0; i < 8; i++)
                builder.CubeBlocks.Add(
                    new MyObjectBuilder_BatteryBlock
                    {
                        SubtypeName = "LargeBlockBatteryBlock",
                        Min = new Vector3I(i, 0, 0),
                        CustomName = new string('x', i + 1),
                        Enabled = true,
                    }
                );
            var live = (IMyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(builder);
            for (int i = 0; i < 8; i++)
            {
                blocks[i] = (IMyFunctionalBlock)live.GetCubeBlock(new Vector3I(i, 0, 0)).FatBlock;
                snapshot[i] = blocks[i].CustomName;
            }
            grid = live.EntityId;
            blocks[0].EnabledChanged += EnabledChanged;
            Log("spawn|" + grid);
        }

        private void Local()
        {
            for (int s = 0; s < Samples; s++)
            {
                int sum = 0;
                long begin = DateTime.UtcNow.Ticks;
                for (int i = 0; i < 16384; i++)
                    sum += blocks[i % 8].CustomName.Length;
                long finish = DateTime.UtcNow.Ticks;
                Log("local|" + s + "|" + begin + "|" + finish + "|" + sum);
                sum = 0;
                begin = DateTime.UtcNow.Ticks;
                for (int i = 0; i < 16384; i++)
                    sum += snapshot[i % 8].Length;
                finish = DateTime.UtcNow.Ticks;
                Log("snapshot|" + s + "|" + begin + "|" + finish + "|" + sum);
            }
        }

        private void Request()
        {
            string payload = "get|" + mode + "|" + seq;
            started = DateTime.UtcNow.Ticks;
            if (mode == "owner")
                MyAPIGateway.Utilities.SendModMessage(
                    Send,
                    new object[] { "to-owner", Channel, payload, grid, (long)seq + 1 }
                );
            else
                ToNode(cachedOwner, payload);
        }

        private static void ToNode(string node, string payload)
        {
            MyAPIGateway.Utilities.SendModMessage(
                Send,
                new object[] { "to-node", Channel, payload, node }
            );
        }

        private void OnMessage(object message)
        {
            long received = DateTime.UtcNow.Ticks;
            var parts = message as object[];
            if (parts == null || parts.Length < 3 || (string)parts[1] != Channel)
                return;
            string kind = (string)parts[0];
            if (kind == "owner")
            {
                cachedOwner = (string)parts[3];
                Log("resolved|" + cachedOwner + "|" + started + "|" + received);
                return;
            }
            if (kind == "fault")
                Log("fault|" + parts[2]);
            if (kind != "to-owner" && kind != "to-node")
                return;
            string origin = (string)parts[2];
            string[] p = ((string)parts[3]).Split('|');
            if (p[0] == "get")
            {
                int s = int.Parse(p[2]);
                int count = p[1] == "batch" ? 8 : 1;
                string[] values = new string[count];
                for (int i = 0; i < count; i++)
                    values[i] = blocks[(s + i) % 8].CustomName;
                string reply =
                    "reply|" + p[1] + "|" + s + "|" + string.Join(",", values) + "|" + received;
                ToNode(origin, reply + "|" + DateTime.UtcNow.Ticks);
            }
            else if (p[0] == "reply" && p[1] == mode && int.Parse(p[2]) == seq)
            {
                Log(
                    mode
                        + "|"
                        + seq
                        + "|"
                        + started
                        + "|"
                        + p[4]
                        + "|"
                        + p[5]
                        + "|"
                        + received
                        + "|"
                        + origin
                        + "|"
                        + p[3]
                );
                if (++seq < Samples)
                    Request();
            }
            else if (p[0] == "event")
                Log((string)parts[3] + "|" + received + "|" + origin);
        }

        private void EnabledChanged(IMyTerminalBlock block)
        {
            long entered = DateTime.UtcNow.Ticks;
            if (!toggling)
                return;
            string payload =
                "event|"
                + events
                + "|"
                + ((IMyFunctionalBlock)block).Enabled
                + "|"
                + setter
                + "|"
                + entered;
            ToNode("node-2", payload + "|" + DateTime.UtcNow.Ticks + "|" + inSetter);
        }

        private static void Log(string line)
        {
            MyLog.Default.WriteLineAndConsole("channel_latency " + line);
        }

        protected override void UnloadData()
        {
            MyAPIGateway.Utilities.UnregisterMessageHandler(Receive, OnMessage);
            if (blocks[0] != null)
                blocks[0].EnabledChanged -= EnabledChanged;
        }
    }
}
