"""Compare actual armor damage with and without an Energy Shields generator."""

import re

# mod_staging first: its bench import puts Cli/ on sys.path for cluster
from mod_staging import command, main, run_staged
from cluster import wait_until

MOD = "484504816"
PROBE = "9991845002"
PATTERN = r"shield_damage_probe control=([\d.E+-]+) shielded=([\d.E+-]+) controlLoss=([\d.E+-]+) shieldLoss=([\d.E+-]+) grids=(\d+),(\d+)"
# X offset of each server's target pair: the two regular nodes simulate theirs 2 km apart
OFFSETS = {"stock": 0, "node-1": 0, "node-2": 2000}


def hit(bench, role):
    """Spawn both targets on ``role``, hit each once and read the damage and integrity loss."""
    command(bench, role, PROBE, "start", str(OFFSETS[role]))
    match = re.search(PATTERN, bench.cluster.wait_node_log(role, PATTERN, 60))
    *values, control, shielded = match.groups()
    row = dict(
        zip(
            ("unshielded", "shielded", "unshielded_loss", "shielded_loss"),
            map(float, values),
            strict=True,
        )
    )
    return row, [int(control), int(shielded)]


def absorbed(row):
    return (
        row["unshielded"] > 0
        and row["shielded"] == 0
        and row["unshielded_loss"] > 0
        and row["shielded_loss"] == 0
    )


def measure(bench, roles, issuer, reference):
    if reference is None:
        row, _ = hit(bench, issuer)
        return {issuer: row, "result": "PASS" if absorbed(row) else "INCONCLUSIVE"}
    # The World Authority owns no partitions and simulates no grids, so the hit runs on each regular
    # node, and the ownership check proves the node that measured is the one simulating the targets.
    report, valid, same = {}, True, True
    for role in (r for r in roles if r != "wa-1"):
        row, grids = hit(bench, role)

        def owners():
            snapshot = bench.diag("entities")
            places = [bench.entity_owner(snapshot, grid) for grid in grids]
            return (
                places if all(len(p) == 1 and p[0][0] == role for p in places) else None
            )

        report[role] = {
            **row,
            "owners": wait_until(owners, 30, f"both targets owned by {role} alone"),
        }
        valid &= row["unshielded"] > 0 and row["unshielded_loss"] > 0
        valid &= row["shielded"] >= 0
        same &= row == reference["stock"]
    report["result"] = "INCONCLUSIVE" if not valid else "PASS" if same else "DIFF"
    return report


def run(corpus, results):
    return run_staged(
        corpus,
        results,
        scenario="SHIELD-DAMAGE",
        title="Energy Shields damage absorption",
        mods=[MOD],
        probe=PROBE,
        fixture="ShieldDamageFixture",
        measure=measure,
    )


if __name__ == "__main__":
    main(run, __doc__)
