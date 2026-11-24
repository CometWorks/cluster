"""Integration checks against the same fixtures as --expect."""

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import zipfile

from corpus import classify


def check(tool: Path) -> None:
    fixtures = Path(__file__).parent / "TestMods"
    policy = {
        row.split("\t")[0]: row.split("\t")
        for row in subprocess.check_output(
            ["dotnet", tool, "--policy-tsv"], text=True
        ).splitlines()
    }
    with tempfile.TemporaryDirectory() as folder:
        archive = Path(folder) / "packed"
        archive.mkdir()
        loose = fixtures / "fixture-d-scan"
        with zipfile.ZipFile(archive / "fixture_legacy.bin", "w") as z:
            for f in loose.rglob("*.cs"):
                z.write(f, f.relative_to(loose).as_posix())
        run = subprocess.check_output(
            ["dotnet", tool, "--evidence", *sorted(fixtures.iterdir()), archive],
            text=True,
        )
        scans = {row["mod"]: row for row in map(json.loads, run.splitlines())}
    assert all(not row["compileErrors"] for row in scans.values())
    scanner = scans["fixture-d-scan"]
    assert scanner["evidence"] == scans["packed"]["evidence"]
    assert scanner["evidence"]["EntityCollections"] == 1
    verdict, breaks, _, _, _ = classify(scanner, policy, True)
    assert (
        verdict == "D" and breaks["B1"] == 1 and breaks["B2"] == 1 and not breaks["B6"]
    )
    assert breaks["B3"] == 1
    members = scanner["evidence"]["Members"]
    overload = "VRage.Game.ModAPI.IMyPlayerCollection.TryGetIdentityId"
    assert (
        members[overload + "(System.Int64)"]
        == members[overload + "(System.UInt64)"]
        == 1
    )
    _, breaks, _, _, _ = classify(scans["fixture-c-setvariable"], policy, True)
    assert breaks["B6"] == 1 and breaks["B7"] == 1
    _, breaks, _, _, _ = classify(scans["fixture-b-gamelogic"], policy, True)
    assert breaks["B4"] == 1
    assert classify(scans["fixture-a-data"], policy, False)[0] == "A"
    assert classify(scans["fixture-a-data"], policy, True)[0] == "U"
    assert classify(dict(scanner, compileErrors=["example"]), policy, True)[0] == "U"
    unresolved = dict(
        scanner,
        evidence=dict(
            scanner["evidence"],
            Members={"example": 1},
            EntityCollections=0,
            MutableLogicFields=0,
            UpdateMembers={},
        ),
    )
    provisional = {
        "example": [
            "example",
            "node-scoped",
            "B1",
            "no",
            "-",
            "1",
            "UNCERTAIN: unverified",
        ]
    }
    verdict, breaks, _, _, unknown = classify(unresolved, provisional, True)
    assert verdict == "U" and not breaks and unknown == 1
    print(
        "PASS archive parity, overload policy lookup, B1/B2/B4/B6/B7, failed and missing IL"
    )


if __name__ == "__main__":
    check(Path(sys.argv[1]))
