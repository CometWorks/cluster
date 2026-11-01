using PluginSdk.Clustering;
using System.Text.Json;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    private static void TestAdmissionDoesNotPauseRelay()
    {
        string root = Path.Combine(Path.GetTempPath(), "relay-admission-" + Guid.NewGuid());
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        try
        {
            var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 28417);
            using var gateway = new DirectGateway(new GatewayOptions(endpoint, endpoint, null, false, null, null, null,
                root, TimeSpan.FromSeconds(10), Admission, null, []));
            var registry = (GatewayRegistry)typeof(DirectGateway).GetField("_registry", fields)!.GetValue(gateway)!;
            var paused = typeof(DirectGateway).GetProperty("RelayPaused", fields)!;
            registry.BeginShutdown(false, TimeSpan.FromSeconds(10));
            Assert(!registry.AdmissionOpen);
            Assert(!(bool)paused.GetValue(gateway)!); // Closing admission alone must not freeze existing sessions.
            typeof(DirectGateway).GetField("_paused", fields)!.SetValue(gateway, true);
            Assert((bool)paused.GetValue(gateway)!);
            typeof(DirectGateway).GetField("_paused", fields)!.SetValue(gateway, false);
            var failure = typeof(GatewayRegistry).GetField("_persistenceFailure", fields)!;
            try
            {
                failure.SetValue(registry, new IOException("injected disk failure"));
                Assert((bool)paused.GetValue(gateway)!); // Durable-state failure still pauses every relay.
            }
            finally { failure.SetValue(registry, null); }
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>T-0256 broadcast roster: the ready nodes and the WA, each with a fence that routes to it; a node that is
    /// not serving is left out, and a caller that is not ready is fenced.</summary>
    // The shape of PluginSdk's PluginNodeInfo; the registry carries only the protocol copy, not the SDK.
    private sealed record PluginNodeView(string Node, long Incarnation, string Role, OwnerFence Fence);

    private static void TestPluginNodes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "plugin-nodes-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            using var registry = new GatewayRegistry(directory, Admission, () => now);
            var wa = registry.RegisterNode(Join("wa-1", "127.0.0.1:28430", Guid.NewGuid()) with
                { Role = NodeRole.WA, ControlEndpoint = "http://127.0.0.1:29430" });
            var first = Register(registry, "node-1", "127.0.0.1:28431", Guid.NewGuid());
            var idle = Register(registry, "node-2", "127.0.0.1:28432", Guid.NewGuid());
            registry.Heartbeat(wa.Node, wa.Epoch, [], serving: true);
            registry.Heartbeat(first.Node, first.Epoch, [], serving: true);
            registry.Heartbeat(idle.Node, idle.Epoch, [], serving: false);
            var request = new PluginRequest { Plugin = "example", Action = "nodes", Node = first.Node, Incarnation = first.Epoch };
            var result = registry.PluginOperation(request);
            Assert(result.Code == PluginResultCode.Success);
            var nodes = JsonSerializer.Deserialize<PluginNodeView[]>(result.Payload)!;
            Assert(nodes.Select(n => n.Node).SequenceEqual(new[] { "node-1", "wa-1" }));
            Assert(nodes.Single(n => n.Node == "wa-1").Role == "WorldAuthority" && nodes.Single(n => n.Node == "node-1").Role == "Regular");
            foreach (var node in nodes)
            {
                Assert(node.Fence.Target.Kind == PluginTargetKind.Node && node.Fence.Node == node.Node && node.Incarnation == node.Fence.Incarnation);
                var message = new PluginMessage { Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Plugin = "example", Topic = "news",
                    Source = first.Node, Incarnation = first.Epoch, Destination = node.Fence };
                Assert(registry.PluginMessageTarget(message, first.Endpoint, out var rejection)?.Node == node.Node && rejection is null);
            }
            request.Node = idle.Node; request.Incarnation = idle.Epoch;
            Assert(registry.PluginOperation(request).Code == PluginResultCode.Fenced);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestPluginServices()
    {
        TestAdmissionDoesNotPauseRelay();
        TestPluginNodes();
        string directory = Path.Combine(Path.GetTempPath(), "plugin-registry-" + Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PluginRequest write;
        PluginRequest lifecycle = null!;
        try
        {
            using (var registry = new GatewayRegistry(directory, Admission, () => now))
            {
                var first = Register(registry, "first", "127.0.0.1:28417", Guid.NewGuid());
                registry.Heartbeat(first.Node, first.Epoch, [], serving: true);
                var read = new PluginRequest { Plugin = "example", Action = "read", Key = "count", Node = first.Node, Incarnation = first.Epoch };
                var missing = registry.PluginOperation(read);
                Assert(missing.Code == PluginResultCode.NotFound);
                write = new PluginRequest { Plugin = read.Plugin, Action = "cas", Key = read.Key, Node = first.Node, Incarnation = first.Epoch,
                    Expected = missing.Record.Version, Payload = [1, 2], SchemaVersion = 1, OperationId = Guid.NewGuid() };
                Assert(registry.PluginOperation(write).Code == PluginResultCode.Success);
                Assert(registry.PluginOperation(write).Record.Version.Revision == 1);
                write.OperationId = Guid.NewGuid();
                Assert(registry.PluginOperation(write).Code == PluginResultCode.Conflict);
                registry.ClaimPartition(42, first.Node, first.Epoch, 0);
                var owner = registry.PluginOperation(new PluginRequest { Plugin = "example", Action = "owner", Node = first.Node,
                    Incarnation = first.Epoch, Target = new PluginTarget { Kind = PluginTargetKind.Partition, Partition = 42 } });
                Assert(owner.Code == PluginResultCode.Success);
                var message = new PluginMessage { Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Plugin = "example", Topic = "echo",
                    Source = first.Node, Incarnation = first.Epoch, Destination = owner.Owner, Payload = [42] };
                Assert(registry.PluginMessageTarget(message, first.Endpoint, out var rejection)?.Node == first.Node && rejection is null);
                Assert(registry.PluginMessageTarget(message, "127.0.0.1:1", out rejection) == null && rejection is null);
                var reply = new PluginMessage { Id = message.Id, OperationId = message.OperationId, Plugin = message.Plugin,
                    Topic = message.Topic, Source = first.Node, Incarnation = first.Epoch, Reply = true, Code = PluginResultCode.Success };
                Assert(registry.PluginReplyValid(reply, first.Endpoint, message, out rejection) && rejection is null);
                Assert(!registry.PluginReplyValid(reply, "127.0.0.1:1", message, out rejection) && rejection is null);
                var originalFence = message.Destination;
                message.Destination = new OwnerFence { StoreId = originalFence.StoreId, Target = originalFence.Target,
                    Node = originalFence.Node, Incarnation = originalFence.Incarnation, Generation = originalFence.Generation + 1 };
                Assert(registry.PluginMessageTarget(message, first.Endpoint, out rejection) == null && rejection == PluginResultCode.Fenced);
                Assert(!registry.PluginReplyValid(reply, first.Endpoint, message, out rejection) && rejection == PluginResultCode.Fenced);
                message.Destination = originalFence;
                foreach (var method in new[] { LiteNetLib.DeliveryMethod.ReliableOrdered, LiteNetLib.DeliveryMethod.ReliableUnordered })
                {
                    var packet = new NodeLinkPacket(NodeLinkMessage.Plugin, 0, 0, JsonSerializer.SerializeToUtf8Bytes(message), method);
                    var decoded = DirectTransportCodec.DecodeNodeLink(DirectTransportCodec.EncodeNodeLink(packet).CopyData(), method);
                    Assert(decoded.Kind == NodeLinkMessage.Plugin && decoded.Payload.SequenceEqual(packet.Payload));
                }
                var validation = registry.PluginOperation(new PluginRequest { Plugin = "example", Action = "validate",
                    Node = first.Node, Incarnation = first.Epoch, Fence = owner.Owner });
                Assert(validation.Code == PluginResultCode.Success && validation.ValidForMilliseconds is > 0 and <= 250);
                write.OperationId = Guid.NewGuid(); write.Key = "owned"; write.Fence = owner.Owner;
                Assert(registry.PluginOperation(write).Code == PluginResultCode.Success);
                registry.PutNodeSlot("plugin-slot", "host", NodeRole.Regular, NodeDesiredState.Wanted);
                registry.ExecutorHeartbeat("executor", [new("plugin-slot", "attempt", SpawnState.Ready, first.Node, null)]);
                lifecycle = new PluginRequest { Plugin = "cluster-lifecycle", Action = "restart", Node = first.Node,
                    Incarnation = first.Epoch, OperationId = Guid.NewGuid(), SaveFirst = false };
                Assert(registry.PluginOperation(lifecycle).Code == PluginResultCode.Unsupported);
                lifecycle.SaveFirst = true;
                Assert(registry.PluginOperation(lifecycle).Code == PluginResultCode.Success);
                Assert(registry.PluginOperation(lifecycle).Code == PluginResultCode.Success); // Lost ack after node closure.
                lifecycle.Action = "stop";
                Assert(registry.PluginOperation(lifecycle).Code == PluginResultCode.Conflict);
                lifecycle.Action = "restart";
                Assert(registry.PluginOperation(write).Code == PluginResultCode.Fenced);
                Assert(registry.PluginMessageTarget(message, first.Endpoint, out rejection) == null && rejection == PluginResultCode.Fenced);
            }
            using var recovered = new GatewayRegistry(directory, Admission, () => now);
            Assert(recovered.PluginOperation(lifecycle).Code == PluginResultCode.Success);
            var second = Register(recovered, "second", "127.0.0.1:28418", Guid.NewGuid());
            recovered.Heartbeat(second.Node, second.Epoch, [], serving: true);
            var result = recovered.PluginOperation(new PluginRequest { Plugin = "example", Key = "count", Action = "read", Node = second.Node, Incarnation = second.Epoch });
            Assert(result.Code == PluginResultCode.Success && result.Record.Payload.SequenceEqual(new byte[] { 1, 2 }));
            Assert(result.Record.Version.StoreId == write.Expected.StoreId);
            write.Node = second.Node; write.Incarnation = second.Epoch;
            Assert(recovered.PluginOperation(write).Code == PluginResultCode.Success); // Durable outcome survives old owner's fence.
            write.OperationId = Guid.NewGuid();
            Assert(recovered.PluginOperation(write).Code == PluginResultCode.Fenced); // New writes cannot reuse the old fence.
            now = now.AddHours(1);
            Assert(recovered.PluginOperation(new PluginRequest { Plugin = "example", Action = "read", Key = "count", Node = second.Node, Incarnation = second.Epoch }).Code == PluginResultCode.Fenced);
        }
        finally { Directory.Delete(directory, true); }
    }
}
