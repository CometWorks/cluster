using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Gui;
using VRage.GameServices;
using VRage.Network;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// SE-943 bench routes: a broadcast controller message sent the way the block sends it, and what this node
/// relays to its clients. The bench's headless clients show no multiplayer chat on their HUD (vanilla
/// MyHudChat ignores it while MyGameService is inactive), so the sender name a client would show is read
/// here, from the ChatMsg the node hands to the client: the client shows CustomData.AuthorName, and without
/// it the name of the message's Author, the node's server member, Good.bot.
/// </summary>
internal static class BroadcastChatRoutes
{
    private static readonly object Lock = new();
    private static readonly List<object> Relayed = new();
    private static bool m_patched;

    public static void Register()
    {
        // POST /broadcast-chat-test?identityId=&author=&text= - what MyChatBroadcastEntityComponent.TransmitMessageToCharacter
        // does on the server, with the block's shown name in CustomData.AuthorName.
        TestRoutes.Add("POST", "/broadcast-chat-test", context =>
        {
            long identityId = TestRoutes.Long(context, "identityId");
            string author = TestRoutes.Query(context, "author");
            string text = TestRoutes.Query(context, "text");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                var customData = new ChatMessageCustomData { AuthorName = author, SenderId = 0, TextColor = Color.Yellow };
                MyMultiplayer.Static.SendChatMessage(text, ChatChannel.BroadcastController, identityId, customData);
                return (object)new { identityId, author, text };
            }));
        });
        // POST /chat-relay-probe - start recording the cluster chat this node relays to its clients.
        TestRoutes.Add("POST", "/chat-relay-probe", context =>
        {
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                if (!m_patched)
                {
                    var original = AccessTools.Method(typeof(MyMultiplayerBaseSeams), nameof(MyMultiplayerBaseSeams.ClusterRelayChat))
                        ?? throw new MissingMethodException("MyMultiplayerBaseSeams.ClusterRelayChat");
                    new Harmony("ClusterTestPlugin.ChatRelayProbe").Patch(original,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(BroadcastChatRoutes), nameof(RelayPrefix))));
                    m_patched = true;
                }
                return (object)new { armed = true };
            }));
        });
        // GET /chat-relay-probe - every relayed message since the probe was armed.
        TestRoutes.Add("GET", "/chat-relay-probe", context =>
        {
            lock (Lock)
                TestRoutes.Json(context, 200, new { relayed = Relayed.ToList() });
        });
    }

    private static void RelayPrefix(ChatMsg msg, ulong recipient)
    {
        lock (Lock)
            Relayed.Add(new
            {
                recipient = recipient.ToString(CultureInfo.InvariantCulture),
                text = msg.Text,
                channel = ((ChatChannel)msg.Channel).ToString(),
                targetId = msg.TargetId,
                hasCustomData = msg.CustomData.HasValue,
                authorName = msg.CustomData?.AuthorName,
            });
    }
}
