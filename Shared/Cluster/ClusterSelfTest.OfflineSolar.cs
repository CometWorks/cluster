using System;
using VRageMath;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
    // Cold game-math regression, not Havok/occlusion or planet differential evidence.
    private static void OfflineSolarPivots()
    {
        Vector3 pivot = new(0, .5f, .45f);
        foreach (MatrixD matrix in new[] { MatrixD.Identity, MatrixD.CreateWorld(new Vector3D(10, 20, 30), Vector3D.Right, Vector3D.Forward) })
        {
            Vector3D expected = matrix.Translation + matrix.Right * pivot.X + matrix.Up * pivot.Y + matrix.Forward * pivot.Z;
            Vector3D actual = OfflineCompensation.SolarPivot(pivot, matrix);
            if (Vector3D.Distance(actual, expected) > 1e-10) throw new InvalidOperationException("solar pivot must use engine Forward Z basis");
            if (Vector3D.Distance(actual, Vector3D.Transform(pivot, matrix)) < .89) throw new InvalidOperationException("solar pivot regression has no sign-sensitive control");
        }
        if (OfflineCompensation.CanMeasureAssembler(true, false, 0)
            || OfflineCompensation.CanMeasureAssembler(false, true, 0)
            || OfflineCompensation.CanMeasureAssembler(false, false, .1)
            || !OfflineCompensation.CanMeasureAssembler(false, false, 0))
            throw new InvalidOperationException("Repeat/disassembly/in-progress queues cannot identify throughput from queue shrinkage");
    }
}
