using System;

namespace ClusterClientDiagnostics;

using CometWorks.ClusterGateway.AdminContract.V1;

public static class DiagnosticsText
{
    public static string Duration(long seconds) => seconds < 0 ? "unknown" : TimeSpan.FromSeconds(Math.Min(seconds, 315360000)).ToString(@"d\d\ hh\:mm\:ss");
    public static string Timestamp(long seconds) => seconds <= 0 ? "unknown" : DateTimeOffset.FromUnixTimeSeconds(Math.Min(seconds, 253402300799)).UtcDateTime.ToString("u");

    public static string Rotation(NodeDiagnostics node, long serverAt, double elapsed)
    {
        if ((node.Rotation == RotationState.Assigned || node.Rotation == RotationState.PolicyEstimate || node.Rotation == RotationState.Disabled)
            && (serverAt <= 0 || node.RotationExpiresAt <= serverAt + (long)Math.Max(0, elapsed)))
            return "Rotation: unavailable";
        switch (node.Rotation)
        {
            case RotationState.Disabled: return "Rotation: disabled";
            case RotationState.HotSpare: return "Rotation: hot spare; lifetime starts on activation";
            case RotationState.NotRotated: return "Rotation: World Authority is not rotated by this rotator";
            case RotationState.Draining: return "Rotation: draining";
            case RotationState.ForceDrainArmed: return "Rotation: force-drain armed";
            case RotationState.NotServing: return "Rotation: waiting to serve";
            case RotationState.Stopped: return "Rotation: node stopped";
            case RotationState.Assigned:
            case RotationState.PolicyEstimate:
                if (node.RotationDueAt <= 0) break;
                // Use the server's clock and time since receipt, independent of the player's wall clock.
                long remaining = node.RotationDueAt - serverAt - (long)Math.Max(0, elapsed);
                string suffix = node.Rotation == RotationState.PolicyEstimate ? " (policy estimate)" : "";
                return remaining >= 0 ? "Rotation due in " + Duration(remaining) + suffix
                    : "Rotation overdue by " + Duration(-remaining) + suffix;
        }
        return "Rotation: unavailable";
    }
}
