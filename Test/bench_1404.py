"""#1404 bench: voxel-journal (N5) request rates with many voxel storages, and edits replicating through the tails path.

Live Octahedron tracks ~2,008 voxel storages per node and the sync loop used to send one GET per storage every
250 ms pass (32.7k/s into the WA with 7 nodes). This bench rebuilds that shape small: WA + two regular nodes,
Cluster-Simple, NO client, T1404_ASTEROIDS asteroids (default 343) dispatched by the WA so every node holds every
one. Then:
  R  N5 requests sent per node and served by the WA over a T1404_WINDOW s window (default 60), and each process's
     CPU, recorded as ``n5-rates``. Old sync loop: ~4 x storages per node; tails: ~4 per node (one per pass).
  E1 node-1 writes asteroid A, node-2 writes asteroid B, then node-2 writes A again over node-1's write: every role
     converges on the last write of each (the known positive - the hashes differ from the pristine ones).
  E2 node-2 restarts: after it is back it holds both edits, no node logged a journal gap, nothing was dropped.
Run it on a package without the fix for the before numbers: the checks pass on both, only ``n5-rates`` differs.

Invocation: cd <tree>/Test && CLUSTER_PACKAGE=<pkg> PYTHONPATH=$PWD uv run python bench_1404.py <label>
"""
import json
import os
import sys
import time
from pathlib import Path

from bench import run_scenario
from cluster import ClusterError, wait_until
from scenarios_core import _lines

LABEL = sys.argv[1] if len(sys.argv) > 1 else "run"
OUT = Path(os.environ.get("T1404_OUT", "/home/factory/cluster-runs/regression/tools/t1404")) / f"bench-{LABEL}"
OUT.mkdir(parents=True, exist_ok=True)
COUNT = int(os.environ.get("T1404_ASTEROIDS", "343"))
WINDOW = float(os.environ.get("T1404_WINDOW", "60"))
ORIGIN = 2_000_000
SPACING = 400
NODES = ["wa-1", "node-1", "node-2"]
CLK = os.sysconf("SC_CLK_TCK")


def node(bench, name: str, path: str, method: str = "GET", timeout: float = 40):
    return bench.cluster.node(name, path, method=method, timeout=timeout)


def attempt(function, default=None):
    try:
        return function()
    except Exception:  # noqa: BLE001 - a polled read that fails is "not yet"
        return default


def poll(predicate, timeout: float, interval: float = 1.0):
    try:
        return wait_until(predicate, timeout, "condition", interval=interval)
    except ClusterError:
        return None


def require(bench, label: str, passed, detail: str = "") -> None:
    if not bench.check(label, bool(passed), detail):
        raise AssertionError(label)


def held(bench, name: str) -> int:
    """Asteroids of the bench's lattice loaded on a node."""
    centre = ORIGIN + SPACING * 3
    rows = attempt(lambda: node(bench, name, f"/voxel-test-asteroids?x={centre}&y={centre}&z={centre}&radius=5000"), {})
    return len((rows or {}).get("asteroids", []))


def query(storage: str) -> str:
    return f"storage={storage}&x=8&y=8&z=8&size=4"


def hashes(bench, storage: str) -> dict:
    return {name: attempt(lambda: node(bench, name, f"/voxel-range-hash?{query(storage)}").get("hash")) for name in NODES}


def write(bench, name: str, storage: str, content: int, material: int) -> str:
    return node(bench, name, f"/voxel-test-write?{query(storage)}&content={content}&material={material}", "POST")["hash"]


def converged(bench, storage: str, want: str, timeout: float = 60) -> dict:
    seen = {}

    def same():
        seen.update(hashes(bench, storage))
        return set(seen.values()) == {want}

    poll(same, timeout)
    return seen


def processes(bench) -> dict:
    """pid of each bench node, by CLUSTER_DATA_ROOT under this run root (only that variable and the node id are read)."""
    root = str(bench.cluster.config.root)
    found = {}
    for pid in filter(str.isdigit, os.listdir("/proc")):
        try:
            if open(f"/proc/{pid}/comm").read().strip() != "MagnetarInterim":
                continue
            raw = open(f"/proc/{pid}/environ", "rb").read().split(b"\0")
        except OSError:
            continue
        env = dict(item.decode(errors="replace").split("=", 1) for item in raw if b"=" in item)
        if env.get("CLUSTER_DATA_ROOT", "").startswith(root + "/") and env.get("CLUSTER_NODE_ID") in NODES:
            found[env["CLUSTER_NODE_ID"]] = int(pid)
    return found


def ticks(pid: int) -> int:
    fields = open(f"/proc/{pid}/stat").read().rpartition(")")[2].split()
    return int(fields[11]) + int(fields[12])


def n5(bench, name: str) -> tuple[int, int]:
    channel = node(bench, name, "/diag")["transport"]["channels"].get("N5", {})
    sent = sum((channel.get(kind) or {}).get("n", 0) for kind in ("local", "lan"))
    served = sum(((channel.get("served") or {}).get(kind) or {}).get("n", 0) for kind in ("local", "lan"))
    return sent, served


def rates(bench) -> dict:
    pids = processes(bench)
    start = {name: (n5(bench, name), ticks(pids[name]) if name in pids else None, time.monotonic()) for name in NODES}
    time.sleep(WINDOW)
    result = {}
    for name, ((sent, served), cpu, started) in start.items():
        (sent_end, served_end), span = n5(bench, name), time.monotonic() - started
        result[name] = {"n5SentPerS": round((sent_end - sent) / span, 1), "n5ServedPerS": round((served_end - served) / span, 1),
                        "cpuPct": round(100 * (ticks(pids[name]) - cpu) / CLK / span) if cpu is not None else None}
    return result


def check(bench):
    bench.start_cluster(nodes=NODES)
    bench.cluster.post("/automatic/pause")  # nothing moves a partition during the run
    side = round(COUNT ** (1 / 3) + 0.5)
    names = []
    for index in range(COUNT):
        x, y, z = (ORIGIN + SPACING * value for value in (index % side, index // side % side, index // side // side))
        names.append(node(bench, "wa-1", f"/voxel-test-spawn-asteroid?x={x}&y={y}&z={z}&radius=16&seed={index + 1}", "POST")["name"])
    counts = {}

    def everywhere():
        counts.update({name: held(bench, name) for name in NODES})
        return all(value >= COUNT for value in counts.values())

    require(bench, f"every role holds the {COUNT} dispatched asteroids", poll(everywhere, 300, interval=5), json.dumps(counts))
    time.sleep(20)  # the sync loop has registered them as tracked storages (Update) for a few passes

    # ---- R: the request rates with COUNT idle storages ----
    measured = rates(bench)
    bench.record("n5-rates", {"asteroids": COUNT, "windowS": WINDOW, "rates": measured})
    bench.check(f"[INFO] N5 over {WINDOW:.0f} s with {COUNT} storages", True, json.dumps(measured))

    # ---- E1: edits replicate in order ----
    storage_a, storage_b = names[COUNT // 3], names[2 * COUNT // 3]
    pristine = {storage: hashes(bench, storage)["wa-1"] for storage in (storage_a, storage_b)}
    first_a = write(bench, "node-1", storage_a, 200, 3)
    seen = converged(bench, storage_a, first_a)
    require(bench, "E1: node-1's write of A reached every role", set(seen.values()) == {first_a} and first_a != pristine[storage_a],
            json.dumps({"want": first_a, "pristine": pristine[storage_a], "seen": seen}))
    hash_b = write(bench, "node-2", storage_b, 180, 2)
    second_a = write(bench, "node-2", storage_a, 90, 5)
    seen_b, seen_a = converged(bench, storage_b, hash_b), converged(bench, storage_a, second_a)
    require(bench, "E1: node-2's write of B and its later write of A over node-1's reached every role",
            set(seen_b.values()) == {hash_b} and set(seen_a.values()) == {second_a} and second_a != first_a and hash_b != pristine[storage_b],
            json.dumps({"B": seen_b, "A": seen_a, "firstA": first_a}))
    entries = node(bench, "wa-1", f"/voxel-journal-entries?storage={storage_a}")
    bench.check("E1: the WA journal of A holds node-1's write then node-2's, in that order", len(entries) == 2
                and entries[0]["sequence"] < entries[1]["sequence"], json.dumps(entries))

    # ---- E2: a node restart keeps both edits ----
    bench.cluster.restart_node("node-2")
    bench.cluster.wait_node_log("node-2", "Game ready", 600)
    seen_a, seen_b = converged(bench, storage_a, second_a, 120), converged(bench, storage_b, hash_b, 120)
    require(bench, "E2: after its restart node-2 holds the last write of A and of B",
            seen_a.get("node-2") == second_a and seen_b.get("node-2") == hash_b, json.dumps({"A": seen_a, "B": seen_b}))
    status = {name: attempt(lambda: node(bench, name, "/voxel-journal-status"), {}) for name in ("node-1", "node-2")}
    gaps = {name: len(_lines(bench, name, "Voxel journal gap")) for name in ("node-1", "node-2")}
    bench.check("E2: no journal gap logged, no outbox drop, nothing queued", not any(gaps.values())
                and all(value.get("outboxDrops") == 0 and value.get("outboundQueued") == 0 for value in status.values()),
                json.dumps({"gaps": gaps, "status": {name: {key: value.get(key) for key in ("outboxDrops", "outboundQueued", "liveSelfPending", "unknownStorageSkips")}
                                                     for name, value in status.items()}}))
    after = rates(bench)
    bench.record("n5-rates-after-restart", {"asteroids": COUNT, "windowS": WINDOW, "rates": after})
    bench.check(f"[INFO] N5 over {WINDOW:.0f} s after the restart", True, json.dumps(after))
    bench.exercised(asteroids=COUNT, storageA=storage_a, storageB=storage_b, rates=measured, ratesAfterRestart=after,
                    tailsRouteMissing={name: len(_lines(bench, name, "tails_route=false")) for name in ("node-1", "node-2")})


result = run_scenario(check, "T1404-voxel-tails", "voxel journal N5 rates with many storages, edits through the tails path", "3", OUT)
(OUT / "verdict.json").write_text(json.dumps(vars(result), default=lambda x: vars(x), indent=2))
raise SystemExit(0 if result.passed else 1)
