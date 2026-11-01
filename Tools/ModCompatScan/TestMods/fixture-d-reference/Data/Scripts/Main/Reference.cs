using System.Collections.Generic;
using Sandbox.Game;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

namespace Fixture
{
    // #1280: fixture-d-scan adapted to the mod channel v1 (Docs/ModChannel.md), the reference adaptation of a
    // class D mod. The World Authority decides and every node acts only on what it owns, so each decision is
    // carried out exactly once cluster-wide:
    // - the WA asks the nodes for their "mod-ref-*" grids (broadcast), each node answers what it sees (to-node),
    //   the WA decides once per grid and sends the action to the grid's owner (to-owner);
    // - the WA follows each decided grid's owner (owner-of) and pings it after a move (to-owner again);
    // - the WA greets every online player once, on the node the player is attached to (player-nodes, to-node);
    // - the owner keeps per-grid state in memory and carries it across a handover (the handover hooks);
    // - every instance logs the resets it is told about.
    // Every line it logs starts with "cluster_mod_reference ", which the P2-MOD-V1 scenario counts.
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class Reference : MySessionComponentBase
    {
        public const string ClusterModCompat = "D adapted";
        private const long SendId = 0x434C55534D4F4453;    // "CLUSMODS"
        private const long ReceiveId = 0x434C55534D4F4452; // "CLUSMODR"
        private const string Channel = "cluster-mod-reference";
        private const string Prefix = "mod-ref-";
        private const int Interval = 300;

        private bool m_asked;
        private bool m_inCluster;
        private string m_role = "";
        private string m_node = "";
        private int m_tick;
        private long m_correlation;
        private int m_operation;
        private int m_ping;
        private string m_players = "";
        // WA: the decided grids and the owner last seen for each; the players already greeted.
        private readonly Dictionary<long, int> m_decided = new Dictionary<long, int>();
        private readonly Dictionary<long, string> m_owners = new Dictionary<long, string>();
        private readonly HashSet<ulong> m_greeted = new HashSet<ulong>();
        // WA: acts the plugin could not deliver (no owner yet), sent again on the next round.
        private readonly List<long> m_retry = new List<long>();
        // Owner: the in-memory per-grid state a game-logic component would hold; vanilla loses it at a handover.
        private readonly Dictionary<long, string> m_acted = new Dictionary<long, string>();

        public override void LoadData()
        {
            MyAPIGateway.Utilities.RegisterMessageHandler(ReceiveId, OnMessage);
        }

        protected override void UnloadData()
        {
            MyAPIGateway.Utilities.UnregisterMessageHandler(ReceiveId, OnMessage);
        }

        public override void UpdateBeforeSimulation()
        {
            if (!m_asked)
            {
                m_asked = true;
                // Answered inside this call; no answer at all means no cluster plugin: stay vanilla.
                Out("role");
                if (m_inCluster) Out("handover-hooks");
            }
            if (!m_inCluster || m_role != "WA" || ++m_tick % Interval != 0) return;
            foreach (long grid in m_retry)
            {
                Log("retry grid=" + grid + " op=" + m_decided[grid]);
                ToOwner(grid, "act:" + m_decided[grid], m_decided[grid]);
            }
            m_retry.Clear();
            MyAPIGateway.Utilities.SendModMessage(SendId, new object[] { "broadcast", Channel, "census" });
            foreach (long grid in m_decided.Keys)
                MyAPIGateway.Utilities.SendModMessage(SendId, new object[] { "owner-of", Channel, grid, ++m_correlation });
            Out("player-nodes");
        }

        private void Out(string kind)
        {
            MyAPIGateway.Utilities.SendModMessage(SendId, new object[] { kind, Channel });
        }

        private void ToNode(string node, string payload)
        {
            MyAPIGateway.Utilities.SendModMessage(SendId, new object[] { "to-node", Channel, payload, node });
        }

        private void ToOwner(long grid, string payload, long correlation)
        {
            MyAPIGateway.Utilities.SendModMessage(SendId, new object[] { "to-owner", Channel, payload, grid, correlation });
        }

        private void Log(string text)
        {
            MyLog.Default.WriteLineAndConsole("cluster_mod_reference " + text + " node=" + m_node);
        }

        private void OnMessage(object message)
        {
            object[] parts = message as object[];
            if (parts == null || parts.Length < 3 || (parts[1] as string) != Channel) return;
            string kind = parts[0] as string;
            switch (kind)
            {
                case "role":
                    m_inCluster = (bool)parts[2];
                    m_role = (string)parts[3];
                    m_node = (string)parts[4];
                    Log("role inCluster=" + m_inCluster + " role=" + m_role + " api=" + parts[5]);
                    break;
                case "broadcast":
                    if ((string)parts[3] == "census" && m_role != "WA") Census((string)parts[2]);
                    break;
                case "to-node":
                    Received((string)parts[2], (string)parts[3]);
                    break;
                case "to-owner":
                    Act((string)parts[3], (long)parts[4]);
                    break;
                case "owner":
                    Owner((long)parts[2], (string)parts[3]);
                    break;
                case "player-nodes":
                    Players((object[])parts[2]);
                    break;
                case "before-handover":
                {
                    long grid = (long)parts[2];
                    string state;
                    if (!m_acted.TryGetValue(grid, out state)) break;
                    MyAPIGateway.Utilities.SendModMessage(SendId, new object[] { "handover-state", Channel, grid, state });
                    Log("before-handover grid=" + grid + " state=" + state);
                    break;
                }
                case "after-import":
                {
                    long grid = (long)parts[2];
                    string state = parts[3] as string;
                    if (state != null) m_acted[grid] = state;
                    Log("after-import grid=" + grid + " state=" + (state ?? "none"));
                    break;
                }
                case "handover-left":
                    m_acted.Remove((long)parts[2]);
                    Log("handover-left grid=" + parts[2]);
                    break;
                case "handover-stayed":
                    Log("handover-stayed grid=" + parts[2]);
                    break;
                case "reset":
                    Log("reset reason=" + parts[2]);
                    break;
                case "fault":
                    Log("fault error=" + parts[2] + " correlation=" + parts[3]);
                    Retry(parts[2] as string, (long)parts[3]);
                    break;
            }
        }

        // Node: what this node sees - which can include another node's grid. The WA dedupes; acting here instead
        // would act once per node that sees the grid.
        private void Census(string origin)
        {
            HashSet<IMyEntity> found = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(found, entity => entity is IMyCubeGrid
                && entity.DisplayName != null && entity.DisplayName.StartsWith(Prefix));
            if (found.Count == 0) return;
            List<string> ids = new List<string>();
            foreach (IMyEntity grid in found) ids.Add(grid.EntityId.ToString());
            ids.Sort();
            ToNode(origin, "grids:" + string.Join(",", ids));
        }

        private void Received(string origin, string payload)
        {
            if (payload.StartsWith("grids:") && m_role == "WA")
            {
                foreach (string id in payload.Substring(6).Split(','))
                {
                    long grid = long.Parse(id);
                    if (m_decided.ContainsKey(grid)) continue;
                    m_decided[grid] = ++m_operation;
                    Log("decide grid=" + grid + " op=" + m_operation + " reportedBy=" + origin);
                    ToOwner(grid, "act:" + m_operation, m_operation);
                }
            }
            else if (payload.StartsWith("greet:"))
            {
                ulong steamId = ulong.Parse(payload.Substring(6));
                long identity = MyAPIGateway.Players.TryGetIdentityId(steamId);
                Log("greet player=" + steamId + " identity=" + identity + " from=" + origin);
                MyVisualScriptLogicProvider.ShowNotification("Hello from the cluster mod reference", 5000, "White", identity);
            }
        }

        // Owner: carry out the WA's decision; the plugin delivered it here because this node owns the grid.
        private void Act(string payload, long grid)
        {
            if (payload.StartsWith("act:"))
            {
                m_acted[grid] = "op" + payload.Substring(4);
                Log("act grid=" + grid + " op=" + payload.Substring(4));
            }
            else if (payload.StartsWith("ping:"))
            {
                string state;
                m_acted.TryGetValue(grid, out state);
                Log("ping grid=" + grid + " seq=" + payload.Substring(5) + " state=" + (state ?? "none"));
            }
        }

        // A to-owner that found no owner was delivered nowhere: the same operation goes again. One whose outcome is
        // unknown may have been delivered, so it is not repeated.
        private void Retry(string error, long correlation)
        {
            if (m_role != "WA" || correlation == 0 || error == null || !error.StartsWith("no node owns")) return;
            foreach (KeyValuePair<long, int> decided in m_decided)
                if (decided.Value == correlation && !m_retry.Contains(decided.Key)) m_retry.Add(decided.Key);
        }

        private void Owner(long grid, string owner)
        {
            string last;
            if (owner == "" || m_owners.TryGetValue(grid, out last) && last == owner) return;
            m_owners[grid] = owner;
            Log("owner grid=" + grid + " owner=" + owner + " previous=" + (last ?? "none"));
            if (last != null) ToOwner(grid, "ping:" + ++m_ping, 0);
        }

        private void Players(object[] players)
        {
            List<string> entries = new List<string>();
            foreach (object entry in players)
            {
                object[] player = (object[])entry;
                ulong steamId = (ulong)player[1];
                string node = (string)player[2];
                entries.Add(player[0] + ":" + steamId + "@" + node);
                if (m_greeted.Add(steamId)) ToNode(node, "greet:" + steamId);
            }
            string listed = string.Join(",", entries);
            if (listed == m_players) return;
            m_players = listed;
            Log("players [" + listed + "]");
        }
    }
}
