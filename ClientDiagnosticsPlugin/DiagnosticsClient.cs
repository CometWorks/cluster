using System;
using System.Diagnostics;
using System.IO;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using CometWorks.ClusterGateway.AdminContract.V1;
using VRage.Game.ModAPI;

namespace ClusterClientDiagnostics;

internal sealed class DiagnosticsClient : IDisposable
{
    private IMyMultiplayer multiplayer;
    private IMySession session;
    private Guid sessionId;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double nextRequest;
    private double receivedAt = -100;
    private string previousNode;
    private long previousEpoch;
    public DiagnosticsSnapshot Snapshot { get; private set; }
    public double DataAge => clock.Elapsed.TotalSeconds - receivedAt;
    public bool Connected => multiplayer != null;
    public bool CanShowAdvanced => Snapshot?.Admin == true && MySession.Static?.IsUserAdmin(multiplayer?.MyId ?? 0) == true;

    public void Update()
    {
        var active = MyAPIGateway.Session != null && MyAPIGateway.Multiplayer?.MultiplayerActive == true
            && !MyAPIGateway.Multiplayer.IsServer ? MyAPIGateway.Multiplayer : null;
        if (!ReferenceEquals(active, multiplayer) || !ReferenceEquals(MyAPIGateway.Session, session))
        {
            multiplayer?.UnregisterSecureMessageHandler(DiagnosticsProtocol.Channel, OnMessage);
            multiplayer = active;
            session = MyAPIGateway.Session;
            sessionId = Guid.NewGuid();
            Snapshot = null;
            previousNode = null;
            previousEpoch = 0;
            receivedAt = -100;
            nextRequest = 0;
            multiplayer?.RegisterSecureMessageHandler(DiagnosticsProtocol.Channel, OnMessage);
        }
        if (multiplayer == null) return;
        if (Snapshot?.Admin == true && !CanShowAdvanced)
        {
            Snapshot.Admin = false;
            Snapshot.Nodes = Array.Empty<NodeDiagnostics>();
            Snapshot.Registry = "";
        }
        if (clock.Elapsed.TotalSeconds < nextRequest) return;
        nextRequest = clock.Elapsed.TotalSeconds + 3;
        multiplayer.SendMessageToServer(DiagnosticsProtocol.Channel, DiagnosticsProtocol.Request(sessionId));
    }

    private void OnMessage(ushort channel, byte[] data, ulong sender, bool fromServer)
    {
        if (!fromServer || multiplayer == null) return;
        try
        {
            DiagnosticsSnapshot incoming = DiagnosticsProtocol.Decode(data, sessionId);
            if (string.IsNullOrWhiteSpace(incoming.Current.Name)) return;
            string name = incoming.Current.Name;
            long epoch = incoming.Current.Epoch;
            if (previousNode != null && (previousNode != name || previousEpoch != 0 && epoch != 0 && previousEpoch != epoch))
                MyAPIGateway.Utilities.ShowNotification("Cluster switched: " + previousNode + " -> " + name, 5000, "White");
            if (previousNode != name || epoch != 0) previousEpoch = epoch;
            previousNode = name;
            Snapshot = incoming;
            receivedAt = clock.Elapsed.TotalSeconds;
        }
        catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is ArgumentException)
        {
            // Another mod may share the channel. Ignore malformed or incompatible packets.
        }
    }

    public void Dispose()
    {
        multiplayer?.UnregisterSecureMessageHandler(DiagnosticsProtocol.Channel, OnMessage);
        multiplayer = null;
        Snapshot = null;
    }
}
