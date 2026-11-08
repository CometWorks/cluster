using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Gui;
using Sandbox.ModAPI;
using VRage.GameServices;
using VRage.Network;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// SE-943 bench routes: a broadcast controller message sent the way the block sends it, and what this node
/// relays to its clients. SE-1261: the chat events this node raises to server plugins and mods. The bench's headless clients show no multiplayer chat on their HUD (vanilla
/// MyHudChat ignores it while MyGameService is inactive), so the sender name a client would show is read
/// here, from the ChatMsg the node hands to the client: the client shows CustomData.AuthorName, and without
/// it the name of the message's Author, the node's server member, Good.bot.
/// </summary>
internal static class BroadcastChatRoutes
{
    private static readonly object Lock = new();
    private static readonly List<object> Relayed = new();
    private static readonly List<object> Events = new();
    private static bool m_patched;
    private static bool m_listening;

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
        // POST /chat-event-probe - start recording the chat this node raises to server plugins (ChatMessageReceived)
        // and mods (MessageRecieved), and refuse lines starting with VetoText the way Hangar refuses vanilla hangar
        // commands: a last-priority prefix on the server's chat request (SE-1261).
        TestRoutes.Add("POST", "/chat-event-probe", context =>
        {
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                if (!m_listening)
                {
                    // Through reflection: the publicized game assembly shows the event and its backing field under one name.
                    Action<ulong, string, ChatChannel, long, ChatMessageCustomData?> received = (sender, text, channel, _, _) => AddEvent("plugin", sender, text, channel);
                    typeof(MyMultiplayerBase).GetEvent(nameof(MyMultiplayerBase.ChatMessageReceived)).AddEventHandler(MyMultiplayer.Static, received);
                    MyAPIGateway.Utilities.MessageRecieved += (sender, text) => AddEvent("mod", sender, text, null);
                    var request = AccessTools.Method(typeof(MyMultiplayerBase), "OnChatMessageReceived_Server", new[] { typeof(ChatMsg) })
                        ?? throw new MissingMethodException("MyMultiplayerBase.OnChatMessageReceived_Server");
                    new Harmony("ClusterTestPlugin.ChatEventProbe").Patch(request,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(BroadcastChatRoutes), nameof(VetoPrefix))) { priority = Priority.Last });
                    m_listening = true;
                }
                return (object)new { armed = true };
            }));
        });
        // GET /chat-event-probe - every chat event raised since the probe was armed.
        TestRoutes.Add("GET", "/chat-event-probe", context =>
        {
            lock (Lock)
                TestRoutes.Json(context, 200, new { events = Events.ToList() });
        });
        // GET /chat-relay-probe - every relayed message since the probe was armed.
        TestRoutes.Add("GET", "/chat-relay-probe", context =>
        {
            lock (Lock)
                TestRoutes.Json(context, 200, new { relayed = Relayed.ToList() });
        });
    }

    private const string VetoText = "SE-1261 veto";

    private static bool VetoPrefix(ChatMsg msg) => msg.Text?.StartsWith(VetoText, StringComparison.Ordinal) != true;

    private static void AddEvent(string kind, ulong sender, string text, ChatChannel? channel)
    {
        lock (Lock)
            Events.Add(new { kind, sender = sender.ToString(CultureInfo.InvariantCulture), text, channel = channel?.ToString() });
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
