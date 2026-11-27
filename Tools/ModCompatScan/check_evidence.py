"""Integration checks against the same fixtures as --expect."""

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import zipfile

from corpus import classify, read_policy

failures = []


def expect(ok: bool, what: str) -> None:
    """Records a failed check; unlike assert, python -O keeps it."""
    if not ok:
        failures.append(what)
        print("FAIL " + what)


def check(tool: Path) -> int:
    fixtures = Path(__file__).parent / "TestMods"
    policy = read_policy()
    with tempfile.TemporaryDirectory() as folder:
        archive = Path(folder) / "packed"
        archive.mkdir()
        loose = fixtures / "fixture-d-scan"
        with zipfile.ZipFile(archive / "fixture_legacy.bin", "w") as z:
            for f in loose.rglob("*.cs"):
                z.write(f, f.relative_to(loose).as_posix())
        direct = Path(folder) / "direct"
        scripts = direct / "Data" / "Scripts"
        scripts.mkdir(parents=True)
        for filename in ("First.cs", "Second.cs"):
            (scripts / filename).write_text("public class RootScript {}")
        run = subprocess.check_output(
            [
                "dotnet",
                tool,
                "--evidence",
                *sorted(fixtures.iterdir()),
                archive,
                direct,
            ],
            text=True,
        )
        scans = {row["mod"]: row for row in map(json.loads, run.splitlines())}
    expect(
        all(not row["compileErrors"] for row in scans.values()),
        'all(not row["compileErrors"] for row in scans.values())',
    )
    expect(
        scans["direct"]["assembly"].split(",")
        == [
            "direct_First.cs",
            "direct_Second.cs",
        ],
        'scans["direct"]["assembly"].split(",") == [ "direct_First.cs", "direct_Second.cs", ]',
    )
    scanner = scans["fixture-d-scan"]
    expect(
        scanner["evidence"] == scans["packed"]["evidence"],
        'scanner["evidence"] == scans["packed"]["evidence"]',
    )
    expect(
        scanner["evidence"]["EntityCollections"] == 1,
        'scanner["evidence"]["EntityCollections"] == 1',
    )
    verdict, breaks, _, _, _ = classify(scanner, policy, True)
    expect(
        (
            verdict == "D"
            and breaks["B1"] == 1
            and breaks["B2"] == 1
            and not breaks["B6"]
        ),
        '( verdict == "D" and breaks["B1"] == 1 and breaks["B2"] == 1 and not breaks["B6"] )',
    )
    expect(breaks["B3"] == 1, 'breaks["B3"] == 1')
    members = scanner["evidence"]["Members"]
    evidence = scanner["evidence"]
    owner = "Sandbox.Definitions.MyCubeBlockDefinition."
    vsl = "Sandbox.Game.MyVisualScriptLogicProvider."
    expect(
        members[owner + "PCU"] == 1 and owner + "PCU" not in evidence["Writes"],
        'members[owner + "PCU"] == 1 and owner + "PCU" not in evidence["Writes"]',
    )
    expect(
        evidence["Writes"][owner + "MaxIntegrity"] == 1,
        'evidence["Writes"][owner + "MaxIntegrity"] == 1',
    )
    expect(
        (
            members[owner + "GuiVisible"] == 2
            and evidence["Writes"][owner + "GuiVisible"] == 1
        ),
        '( members[owner + "GuiVisible"] == 2 and evidence["Writes"][owner + "GuiVisible"] == 1 )',
    )
    expect(
        evidence["IndirectAccess"][owner + "IntegrityPointsPerSec"] == 1,
        'evidence["IndirectAccess"][owner + "IntegrityPointsPerSec"] == 1',
    )
    expect(
        evidence["IndirectAccess"][owner + "MountPoints"] == 1,
        'evidence["IndirectAccess"][owner + "MountPoints"] == 1',
    )
    expect(
        (
            members[vsl + "PlayerDisconnected"] == 2
            and evidence["Writes"][vsl + "PlayerDisconnected"] == 1
        ),
        '( members[vsl + "PlayerDisconnected"] == 2 and evidence["Writes"][vsl + "PlayerDisconnected"] == 1 )',
    )
    expect(
        (
            members[vsl + "PlayerConnected"] == 1
            and vsl + "PlayerConnected" not in evidence["Writes"]
        ),
        '( members[vsl + "PlayerConnected"] == 1 and vsl + "PlayerConnected" not in evidence["Writes"] )',
    )
    expect(
        breaks["B7"] == 2 and breaks["B8"] == 1,
        'breaks["B7"] == 2 and breaks["B8"] == 1',
    )
    overload = "VRage.Game.ModAPI.IMyPlayerCollection.TryGetIdentityId"
    expect(
        (
            members[overload + "(System.Int64)"]
            == members[overload + "(System.UInt64)"]
            == 1
        ),
        '( members[overload + "(System.Int64)"] == members[overload + "(System.UInt64)"] == 1 )',
    )
    _, breaks, _, _, _ = classify(scans["fixture-c-setvariable"], policy, True)
    expect(
        breaks["B6"] == 1 and breaks["B7"] == 1,
        'breaks["B6"] == 1 and breaks["B7"] == 1',
    )
    _, breaks, _, _, _ = classify(scans["fixture-b-gamelogic"], policy, True)
    expect(breaks["B4"] == 1, 'breaks["B4"] == 1')
    expect(
        classify(scans["fixture-a-data"], policy, False)[0] == "A",
        'classify(scans["fixture-a-data"], policy, False)[0] == "A"',
    )
    expect(
        classify(scans["fixture-a-data"], policy, True)[0] == "U",
        'classify(scans["fixture-a-data"], policy, True)[0] == "U"',
    )
    expect(
        classify(dict(scanner, compileErrors=["example"]), policy, True)[0] == "U",
        'classify(dict(scanner, compileErrors=["example"]), policy, True)[0] == "U"',
    )
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
        "example": {
            "member": "example",
            "policy": "node-scoped",
            "breakage": "B1",
            "shim": "no",
            "channel": "-",
            "on": "any",
            "mods": "1",
            "reason": "UNCERTAIN: unverified",
        }
    }
    verdict, breaks, _, _, unknown = classify(unresolved, provisional, True)
    expect(
        verdict == "U" and not breaks and unknown == 1,
        'verdict == "U" and not breaks and unknown == 1',
    )
    if failures:
        print(f"FAIL ({len(failures)} checks)")
        return 1
    print(
        "PASS archive parity, root scripts, overload policy, field and delegate mutation, B1/B2/B4/B6/B7/B8, incomplete IL"
    )
    return 0


if __name__ == "__main__":
    sys.exit(check(Path(sys.argv[1])))
