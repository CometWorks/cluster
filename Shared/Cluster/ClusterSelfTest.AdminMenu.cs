using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Entities;
using VRageMath;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
    /// <summary>
    /// SE-1596, the pure parts of the cluster-wide admin Entity List and PCU tab: a node's rows survive the paged
    /// gzip round trip in order, no page passes the bus's 64 KiB, a page past a node's last is a bare count; the merge
    /// lists each id once, a node's row replaces the local copy of its entity, an offline row only fills a gap, remote
    /// and offline rows are tagged; the PCU list keeps vanilla's "(PCU) name" order with offline grids last and out of
    /// the total; the chat lines.
    /// </summary>
    private static void AdminMenuRules()
    {
        void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException("admin-menu: " + message);
        }
        var random = new Random(1596);
        AdminMenuService.Row Row(long id, string name) =>
            new()
            {
                Id = id,
                Name = name,
                Blocks = random.Next(1, 20000),
                Pcu = random.Next(1, 100000),
                Mass = random.Next(1, 10_000_000),
                X = Math.Round(random.NextDouble() * 2e6 - 1e6),
                Y = Math.Round(random.NextDouble() * 2e6 - 1e6),
                Z = Math.Round(random.NextDouble() * 2e6 - 1e6),
                OwnerName = "Player " + random.Next(1, 300),
                Owner = 144115188075855872 + random.Next(),
                Speed = (float)Math.Round(random.NextDouble() * 100, 1),
                Distance = random.Next(0, 1_000_000),
                Login = random.Next(0, 100000),
                Replicated = true,
                Grid = true,
            };

        // Pages: 5000 grid rows of a busy node, names like Octahedron's
        var rows = Enumerable
            .Range(0, 5000)
            .Select(i => Row(100_000_000_000 + i * 7919L, "Trader Ship " + i + " (Hauler)"))
            .ToList();
        var watch = Stopwatch.StartNew();
        byte[][] pages = AdminMenuService.Pages(rows);
        long packMs = watch.ElapsedMilliseconds;
        Check(pages.Length > 1, "5000 rows need more than one page, got " + pages.Length);
        Check(
            pages.All(page => page.Length <= AdminMenuService.MaxPagePayload),
            "a page is over the limit: " + pages.Max(page => page.Length)
        );
        var back = new List<AdminMenuService.Row>();
        foreach (byte[] page in pages)
        {
            (int count, AdminMenuService.Row[] got) = AdminMenuService.ReadPage(page);
            Check(count == pages.Length, "every page carries the page count");
            back.AddRange(got);
        }
        Check(
            back.Select(row => (row.Id, row.Name, row.Pcu, row.X))
                .SequenceEqual(rows.Select(row => (row.Id, row.Name, row.Pcu, row.X))),
            "the rows come back whole and in order"
        );
        Check(
            AdminMenuService.Pages(Array.Empty<AdminMenuService.Row>()) is [var empty]
                && AdminMenuService.ReadPage(empty) is (1, { Length: 0 }),
            "no rows is one empty page"
        );
        Check(
            AdminMenuService.ReadPage(new byte[] { 3, 0 }) is (3, { Length: 0 }),
            "a page past a node's last is a bare count"
        );
        Check(
            AdminMenuService.Pages(rows.Take(300).ToList(), rawBudget: 1 << 30).Length >= 1
                && AdminMenuService
                    .Pages(rows, rawBudget: 1 << 30)
                    .All(page => page.Length <= AdminMenuService.MaxPagePayload),
            "a page too big gzipped is split until it fits"
        );
        Console.WriteLine(
            $"[ClusterPlugin] self-test admin-menu: 5000 rows -> {pages.Length} pages,"
                + $" {pages.Sum(page => (long)page.Length)} bytes, {(double)pages.Sum(page => (long)page.Length) / rows.Count:F1} bytes/row,"
                + $" packed in {packMs} ms"
        );

        // Merge
        MyEntityList.MyEntityListInfoItem Local(long id, string name) =>
            new() { EntityId = id, DisplayName = name };
        var local = new List<MyEntityList.MyEntityListInfoItem>
        {
            Local(1, "Here"),
            Local(2, "Copy of B's"),
            Local(-1, "GPS a: mark"),
            Local(9, "Planet"),
        };
        AdminMenuService.Row third = Row(3, "Third");
        var merged = AdminMenuService.Merge(
            local,
            new (string, IReadOnlyList<AdminMenuService.Row>)[]
            {
                ("node-3", new[] { third, Row(2, "B twice") }),
                ("node-2", new[] { Row(2, "B's"), Row(4, "Fourth"), Row(-5, "gps") }),
            },
            new[] { Row(4, "Woke"), Row(6, "Asleep") }
        );
        Check(
            merged
                .Select(row => (row.EntityId, row.DisplayName))
                .SequenceEqual(
                    new (long, string)[]
                    {
                        (1, "Here"),
                        (2, "B's [node-2]"),
                        (-1, "GPS a: mark"),
                        (9, "Planet"),
                        (4, "Fourth [node-2]"),
                        (3, "Third [node-3]"),
                        (6, "Asleep [offline]"),
                    }
                ),
            "merge: "
                + string.Join(" | ", merged.Select(row => row.EntityId + " " + row.DisplayName))
        );
        Check(
            merged.Single(row => row.EntityId == 3).Position
                == new Vector3D(third.X, third.Y, third.Z),
            "a remote row keeps its position for the camera jump"
        );

        // PCU tab
        AdminMenuService.GridPcu Pcu(long id, int pcu, string name) => new(pcu, name, id, 0, 0, 0);
        var (names, ids, total) = AdminMenuService.MergeGrids(
            new[] { Pcu(1, 50, "Small") },
            new[] { ("node-2", Pcu(2, 900, "Big")), ("node-3", Pcu(1, 50, "dupe")) },
            new[]
            {
                new AdminMenuService.OfflineGrid(7, "Zed", true, 10, 0, 0, 0, null, null),
                new AdminMenuService.OfflineGrid(8, "Alpha", true, 10, 0, 0, 0, null, null),
                new AdminMenuService.OfflineGrid(2, "Big", true, 10, 0, 0, 0, null, null),
            }
        );
        Check(
            names.SequenceEqual(
                new[] { "(900) Big [node-2]", "(50) Small", "(offline) Alpha", "(offline) Zed" }
            )
                && ids.SequenceEqual(new long[] { 2, 1, 8, 7 })
                && total == 950,
            "pcu list: " + string.Join(" | ", names) + " total " + total
        );

        // Chat lines
        Check(
            AdminMenuService.Outcome(
                MyEntityList.EntityListAction.Remove,
                new Dictionary<string, int> { ["node-2"] = 2 },
                0,
                1,
                1,
                Array.Empty<string>()
            )
                == "Admin Remove: done, 2 on node-2. 1 in an offline area, skipped. 1 not found on any server.",
            "outcome line"
        );
        Check(
            AdminMenuService.Incomplete(Array.Empty<string>(), Array.Empty<string>()) == null,
            "a complete list says nothing"
        );
        Check(
            AdminMenuService.Incomplete(
                new[] { "node-4", AdminMenuService.OfflineTag },
                new[] { "node-2" }
            )
                == "Admin list: No answer from node-4, so they are not listed. Grids in offline areas could not be"
                    + " listed. Too many rows on node-2: only the first 32 pages are listed.",
            "incomplete line"
        );
    }
}
