using System.Text;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    private static void TestClientDiagnostics()
    {
        void Refused(Action action)
        {
            try { action(); }
            catch (Exception error) when (error is RegistryConflictException or ArgumentException) { return; }
            throw new InvalidOperationException("Expected a refused diagnostics operation.");
        }
        using var f = new NodeControlFixture("diagnostics");
        NodeRegistration first = f.Boot("node-1");
        NodeRegistration other = f.Boot("node-2");
        var assignment = new Admin.RotationAssignment(first.Node, first.Epoch, f.Now.AddMinutes(10));
        var publication = new Admin.RotationPublication(0, "cluster-manager", f.Now.AddSeconds(60), true, [assignment]);
        Assert(f.Registry.PublishRotation(publication) == 1);
        Admin.DiagnosticsSnapshot snapshot = f.Registry.ClientDiagnostics(first.Node, first.Epoch);
        Assert(snapshot.Current.Rotation == Admin.RotationState.Assigned && snapshot.TotalNodes == 2);
        Assert(snapshot.Current.RotationDueAt == assignment.DueAt.ToUnixTimeSeconds());
        Assert(snapshot.Current.RotationExpiresAt == publication.ValidUntil.ToUnixTimeSeconds());
        Refused(() => f.Registry.PublishRotation(publication));
        Refused(() => f.Registry.PublishRotation(publication with { ExpectedRevision = 1, Nodes = [assignment with { Epoch = first.Epoch + 1 }] }));
        Refused(() => f.Registry.PublishRotation(publication with { ExpectedRevision = 1, Nodes = [assignment, assignment] }));
        Refused(() => f.Registry.PublishRotation(publication with { ExpectedRevision = 1, ValidUntil = f.Now.AddHours(1) }));
        Refused(() => f.Registry.PublishRotation(publication with { ExpectedRevision = 1, Enabled = false }));
        Refused(() => f.Registry.ClientDiagnostics(first.Node, first.Epoch + 1));
        // Readers get copies; a caller cannot modify durable publication state.
        var read = f.Registry.RotationStatus();
        read.Nodes[0] = assignment with { Epoch = -1 };
        Assert(f.Registry.RotationStatus().Nodes[0].Epoch == first.Epoch);
        f.Reopen();
        Assert(f.Registry.RotationStatus().Revision == 1 && f.Registry.RotationStatus().Nodes.Length == 0);
        f.Heartbeat(first.Node, serving: true);
        f.Heartbeat(other.Node, serving: true);
        Assert(f.Registry.ClientDiagnostics(first.Node).Current.RotationDueAt == assignment.DueAt.ToUnixTimeSeconds());
        var session = Guid.NewGuid();
        byte[] basic = Admin.DiagnosticsProtocol.Encode(snapshot, session, false);
        Assert(!Encoding.UTF8.GetString(basic).Contains(other.Node));
        Assert(Admin.DiagnosticsProtocol.Decode(basic, session).Nodes.Length == 0);
        Assert(Admin.DiagnosticsProtocol.Decode(Admin.DiagnosticsProtocol.Encode(snapshot, session, true), session).Nodes.Length == 2);
        f.Now += TimeSpan.FromSeconds(61);
        Assert(f.Registry.RotationStatus().Nodes.Length == 0);
        Assert(f.Registry.ClientDiagnostics(first.Node).Current.Rotation == Admin.RotationState.Unknown);
        Assert(f.Registry.PublishRotation(publication with { ExpectedRevision = 1, ValidUntil = f.Now.AddSeconds(60), Enabled = false, Nodes = [] }) == 2);
        Assert(f.Registry.ClientDiagnostics(first.Node).Current.Rotation == Admin.RotationState.Disabled);
        f.Registry.PublishRotation(publication with { ExpectedRevision = 2, ValidUntil = f.Now.AddSeconds(60) });
        f.Registry.CloseNode(first.Node, first.Epoch);
        Assert(f.Registry.ClientDiagnostics(first.Node).Current.Rotation == Admin.RotationState.Draining);
        Assert(f.Registry.RotationStatus().Nodes.Length == 0);
    }
}
