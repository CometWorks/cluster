"""SE-1158: the Gateway's DLC app ids (KnownDlcs.AppIds in C#, shared by the Gateway and the nodes, DLC_APP_IDS in Rust) must
match each other and the game's Content/Data/Game/DLCs.sbc. A DLC missing from them counts as owned
by every player on the nodes. Run after every game update:

    python3 Test/check_dlc_ids.py [path/to/DLCs.sbc]

Without an argument it reads the Dedicated Server's copy from the default Steam folder.
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_SBC = (
    Path.home()
    / ".steam/steam/steamapps/common/SpaceEngineersDedicatedServer/Content/Data/Game/DLCs.sbc"
)


def ids(path: Path, start: str, end: str) -> list[int]:
    text = path.read_text(encoding="utf-8")
    body = text[text.index(start) :]
    return [int(n) for n in re.findall(r"\d+", body[len(start) : body.index(end)])]


def main() -> int:
    csharp = ids(ROOT / "Shared/Cluster/KnownDlcs.cs", "AppIds =", "];")
    rust = ids(
        ROOT / "GatewayRs/gateway-rs/src/steam.rs", "DLC_APP_IDS: &[u32] = &[", "];"
    )
    sbc = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_SBC
    game = [int(n) for n in re.findall(r"<AppId>(\d+)</AppId>", sbc.read_text("utf-8"))]
    ok = True
    for name, values in (("C#", csharp), ("Rust", rust)):
        missing = sorted(set(game) - set(values))
        extra = sorted(set(values) - set(game))
        if missing or extra:
            ok = False
            print(f"{name}: missing {missing}, not in the game {extra}")
    print(f"{len(game)} DLCs in {sbc}: {'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
