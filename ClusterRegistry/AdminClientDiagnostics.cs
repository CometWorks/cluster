using System.Net;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace ClusterRegistry;

internal sealed partial class DirectGateway
{
    private void AdminPublishRotation(HttpListenerContext context, AdminActor actor)
    {
        var request = ReadAdminBody<Admin.RotationPublication>(context);
        if (request == null)
        {
            WriteAdminError(context, 400, "invalid_arguments", "A rotation publication is required.");
            return;
        }
        var record = BeginMutation(context, actor, "rotation", Fingerprint(request), "rotation", out bool replayed);
        if (record == null) return;
        if (!replayed)
        {
            try
            {
                long revision = _registry!.PublishRotation(request);
                _registry.CompleteAdminOperation(record.OperationId, new Dictionary<string, string> { ["revision"] = revision.ToString() });
            }
            catch (ArgumentException error) { _registry!.FailAdminOperation(record.OperationId, "invalid_arguments", error.Message); }
            catch (RegistryConflictException error) { _registry!.FailAdminOperation(record.OperationId, "rotation_conflict", error.Message); }
        }
        WriteOperation(context, record);
    }
}
