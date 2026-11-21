using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
    /// <summary>
    /// SE-1759: the round-trip gate's walk over a grid of armor blocks, which carry no EntityId and so are paired by
    /// position. It must stay linear in the block count (it was quadratic: 6.9 s for 60,000 blocks) and still name
    /// a differing block by its index.
    /// </summary>
    private static void BigGridCompare()
    {
        static string Grid(int blocks, int changed = -1)
        {
            StringBuilder xml = new StringBuilder(DriftHead)
                .Append(
                    "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId>"
                )
                .Append("<GridSizeEnum>Large</GridSizeEnum><CubeBlocks>");
            for (int index = 0; index < blocks; index++)
                xml.Append(
                        "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_CubeBlock\"><SubtypeName>LargeBlockArmorBlock"
                    )
                    .Append("</SubtypeName><Min x=\"")
                    .Append(index % 40)
                    .Append("\" y=\"")
                    .Append(index / 40 % 40)
                    .Append("\" z=\"")
                    .Append(index / 1600)
                    .Append("\" /><ColorMaskHSV x=\"0\" y=\"-0.8\" z=\"")
                    .Append(index == changed ? "0.1" : "0.55")
                    .Append("\" /></MyObjectBuilder_CubeBlock>");
            return xml.Append("</CubeBlocks></MyObjectBuilder_EntityBase>")
                .Append(DriftTail)
                .ToString();
        }
        double Seconds(int blocks)
        {
            string grid = Grid(blocks);
            Stopwatch watch = Stopwatch.StartNew();
            List<string> same = ImportRoundTrip.ResidualsOfXml(grid, grid);
            Assert(same.Count == 0, $"{blocks} equal armor blocks reported: " + DriftReport(same));
            return watch.Elapsed.TotalSeconds;
        }
        Seconds(1000); // JIT
        double small = Seconds(10_000),
            large = Seconds(40_000);
        // Linear is about 4x for 4x the blocks, quadratic 16x; small absolute times are noise.
        Assert(
            large < 0.5 || large < 8 * small,
            $"the walk is not linear in the block count: 10,000 blocks {small:F3} s, 40,000 blocks {large:F3} s"
        );
        List<string> residuals = ImportRoundTrip.ResidualsOfXml(Grid(60_000), Grid(60_000, 59_999));
        Assert(
            residuals.Count == 1
                && residuals[0].Contains("/MyObjectBuilder_CubeBlock[59999]/ColorMaskHSV/@z: "),
            "a changed block among 60,000 must be named by its index, got: "
                + DriftReport(residuals)
        );
    }
}
