using System.Buffers.Binary;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    /// <summary>SE-1759: the import deadline follows the sector's declared body bytes, with the node call
    /// timeout as its floor and a cap a corrupt header cannot pass.</summary>
    private static void TestImportTimeout()
    {
        static byte[] Sector(long body, int stored = 1000)
        {
            byte[] sector = new byte[100 + stored + 16];
            "MPS1"u8.CopyTo(sector);
            BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(84), body);
            return sector;
        }
        static void Check(bool value, string what)
        {
            if (!value)
                throw new InvalidOperationException("import-timeout: " + what);
        }
        double floor = DirectGateway.NodeCallTimeout.TotalSeconds;
        double rate = DirectGateway.ImportBodyBytesPerSecond;
        Check(
            Math.Abs(DirectGateway.ImportTimeout(Sector(0)).TotalSeconds - floor - 1116 / rate)
                < 0.001,
            "an empty body costs the sector's own bytes on top of the floor"
        );
        long body = 64L << 20;
        Check(
            DirectGateway.SectorBodyBytes(Sector(body)) == body,
            "the body length is read from the header"
        );
        Check(
            Math.Abs(DirectGateway.ImportTimeout(Sector(body)).TotalSeconds - floor - body / rate)
                < 0.001,
            "64 MiB of body is the floor plus body / rate"
        );
        Check(
            DirectGateway.ImportTimeout(Sector(body * 2))
                > DirectGateway.ImportTimeout(Sector(body)),
            "a bigger body gets a longer deadline"
        );
        byte[] plain = new byte[5000];
        Check(
            DirectGateway.SectorBodyBytes(plain) == plain.Length,
            "a sector without the MPS1 header counts its own length"
        );
        Check(
            DirectGateway.ImportTimeout(Sector(long.MaxValue / 2))
                == DirectGateway.ImportTimeoutCap,
            "a corrupt body length is capped"
        );
    }
}
