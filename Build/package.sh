#!/usr/bin/env bash
# Build the Magnetar cluster deployment archive: one tar.zst holding the Magnetar launcher, the
# cluster plugin sources (compiled by Magnetar on the target host), the Gateway (ClusterRegistry
# project), the MagnetarWorld tool, the cluster CLI and the documentation - and no
# game binaries or content (the user supplies those; see Build/archive-README.md, packaged as
# the archive's README.md).
#
# Usage: Build/package.sh [output-dir]
# Direct Transport is NOT staged: nodes and clients both take it from their hubs
# (CometWorks/magnetar-hub serves the ServerPlugin, StarCpt/PluginHub the ClientPlugin), and the
# manifest records the commit that pin resolves to. Staging a checkout's sources was the old
# arrangement and it let the two sides run different transport builds (Viktor 2026-09-20).
#
# Env:   MAGNETAR_HOME          Magnetar install tree to package    (default ~/.config/Magnetar)
#        LINUX_DEPENDENCIES     Build/fetch-linux-dependencies.sh destination (default: its default)
#        CONFIGURATION          build configuration                  (default Release)
set -euo pipefail

CLUSTER_REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MAGNETAR_HOME="${MAGNETAR_HOME:-$HOME/.config/Magnetar}"
STEAM_DEPENDENCIES="${LINUX_DEPENDENCIES:-${XDG_CACHE_HOME:-$HOME/.cache}/cluster/linux-dependencies}/steam"
CONFIGURATION="${CONFIGURATION:-Release}"
OUT_DIR="${1:-$CLUSTER_REPO/Build/out}"

# The hub pin both sides must agree on. Read at package time so the manifest names a BUILD rather
# than the string "hub", which would name none - the same rule and the same two hubs as
# cluster-regression/bin/release-package.
hub_pin() {
    local xml pin
    xml="$(curl -fsS -m 30 "$1")" || { echo "REFUSING: cannot read the hub manifest $1" >&2; exit 1; }
    pin="$(printf '%s' "$xml" | grep -oP '(?<=<Commit>)[0-9a-f]{40}(?=</Commit>)' | head -1)"
    [[ -n "$pin" ]] || { echo "REFUSING: $1 pins no 40-hex commit" >&2; exit 1; }
    printf '%s' "$pin"
}

for tool in dotnet tar zstd git python3 curl; do
    command -v "$tool" >/dev/null || { echo "required tool not found: $tool" >&2; exit 1; }
done
[[ -x "$MAGNETAR_HOME/MagnetarInterim.bin" ]] || { echo "no Magnetar install at $MAGNETAR_HOME (build ~/ws/se/se1/magnetar first)" >&2; exit 1; }
for f in Steamworks.NET.dll libsteam_api.so; do
    [[ -f "$MAGNETAR_HOME/Libraries/MagnetarInterim/$f" ]] && continue
    [[ -f "$STEAM_DEPENDENCIES/$f" ]] || { echo "Linux Steam dependency $f is neither in the Magnetar tree nor in $STEAM_DEPENDENCIES (Build/fetch-linux-dependencies.sh)" >&2; exit 1; }
done

VERSION="$(git -C "$CLUSTER_REPO" describe --tags --always --dirty)"
NAME="magnetar-cluster-$VERSION"
STAGING="$OUT_DIR/$NAME"

# Refuse to rebuild under a running cluster: a live process has files open in the staging
# directory the rm -rf below would delete. Package elsewhere and rename instead.
if [ -d "$STAGING" ] && [ -z "${MAGNETAR_PACKAGE_FORCE:-}" ]; then
    for cmdline in /proc/[0-9]*/cmdline; do
        if tr '\0' '\n' < "$cmdline" 2>/dev/null | grep -qF "$STAGING/"; then
            echo "REFUSING to package into $STAGING: a running process (${cmdline%/cmdline}) has files open there." >&2
            echo "  Package to a scratch dir and rename, e.g. Build/package.sh $CLUSTER_REPO/Build/out-tmp" >&2
            exit 1
        fi
    done
fi

rm -rf "$STAGING"
mkdir -p "$STAGING"

echo "== Building ($CONFIGURATION)"
# The plugins are compiled by Magnetar on the target host from the packaged sources; building
# them here only proves the sources compile against the publicized game assemblies - and against the
# PluginSdk of the Magnetar tree this archive ships (MAGNETAR_HOME), not whatever ~/.config/Magnetar holds.
dotnet build "$CLUSTER_REPO/ClusterNodePlugin/ClusterNodePlugin.csproj" -c "$CONFIGURATION" --nologo -v q -p:RunPostBuildEvent=Never -p:Magnetar="$MAGNETAR_HOME"
dotnet build "$CLUSTER_REPO/WorldAuthorityPlugin/WorldAuthorityPlugin.csproj" -c "$CONFIGURATION" --nologo -v q -p:RunPostBuildEvent=Never -p:Magnetar="$MAGNETAR_HOME"
dotnet build "$CLUSTER_REPO/Tools/MagnetarWorld/MagnetarWorld.csproj" -c "$CONFIGURATION" --nologo -v q
dotnet build "$CLUSTER_REPO/ClusterRegistry/ClusterRegistry.csproj" -c "$CONFIGURATION" -p:MagnetarPackaging=true --nologo -v q

TOOL_OUT="$CLUSTER_REPO/Tools/MagnetarWorld/bin/$CONFIGURATION/net10.0"
REGISTRY_OUT="$CLUSTER_REPO/ClusterRegistry/bin/$CONFIGURATION/net10.0"

echo "== Staging"
mkdir -p "$STAGING/magnetar" "$STAGING/plugins/cluster" \
         "$STAGING/registry" "$STAGING/tools/MagnetarWorld" "$STAGING/cli" "$STAGING/docs" "$STAGING/examples"
# Magnetar: the portable install tree without its config dir (per-node config dirs are created
# by the CLI) and without any preloader/plugin caches.
tar -C "$MAGNETAR_HOME" --exclude=./Magnetar -cf - . | tar -C "$STAGING/magnetar" -xf -
for f in Steamworks.NET.dll libsteam_api.so; do
    [[ -f "$STAGING/magnetar/Libraries/MagnetarInterim/$f" ]] || cp -a "$STEAM_DEPENDENCIES/$f" "$STAGING/magnetar/Libraries/MagnetarInterim/"
done
# Plugin sources: tracked files only (Magnetar compiles whatever *.cs it finds under the
# manifest's SourceDirectories; bin/obj never travel).
git -C "$CLUSTER_REPO" archive HEAD | tar -x -C "$STAGING/plugins/cluster"
rm -rf "$STAGING/plugins/cluster/Build/out"
# Magnetar compiles only the manifests' SourceDirectories. The Gateway ships built under registry/,
# its docs under docs/ and its test world under examples/, so none of it travels as plugin source.
rm -rf "$STAGING/plugins/cluster/ClusterRegistry" "$STAGING/plugins/cluster/ClusterRegistry.AdminContract" \
       "$STAGING/plugins/cluster/ClusterRegistry.Cli" "$STAGING/plugins/cluster/Tools/ColocationReplay" \
       "$STAGING/plugins/cluster/Docs/Gateway" "$STAGING/plugins/cluster/Build/dist" "$STAGING/plugins/cluster/Test"
# Magnetar lists a dev folder's files with `git ls-files --cached --others --exclude-standard`
# and gives up when the list is empty (no fallback in that case). An archive unpacked inside a
# repository - Build/out is inside this one - would list nothing, because the parent ignores it.
# A one-commit repository per plugin folder makes the listing complete wherever it is unpacked.
for plugin in cluster; do
    git -C "$STAGING/plugins/$plugin" init -q
    git -C "$STAGING/plugins/$plugin" -c user.name=package -c user.email=package@localhost add -A
    git -C "$STAGING/plugins/$plugin" -c user.name=package -c user.email=package@localhost commit -q -m "packaged $VERSION"
done
cp -a "$TOOL_OUT/." "$STAGING/tools/MagnetarWorld/"
# The regression harness (cluster-regression/regression/env.py) points MAGNETAR_WORLD_TOOL at
# server/MagnetarWorld.dll of the archive it pins; keep that path valid without a harness change.
ln -s tools/MagnetarWorld "$STAGING/server"
cp -a "$REGISTRY_OUT/." "$STAGING/registry/"
cp -a "$CLUSTER_REPO/Cli/cluster.py" "$CLUSTER_REPO/Cli/gateway_supervisor.py" "$CLUSTER_REPO/Cli/magnetar_config.py" "$CLUSTER_REPO/Cli/cluster_hostd.py" "$CLUSTER_REPO/Cli/deployment-capabilities.json" "$STAGING/cli/"
cp -a "$CLUSTER_REPO/Cli/cluster" "$STAGING/cli/cluster"
chmod +x "$STAGING/cli/cluster"
cp -a "$CLUSTER_REPO/Build/archive-README.md" "$STAGING/README.md"
for doc in ClusterCLI.md GatewayAPI.md NodeAPI.md EnvVars.md; do
    cp -a "$CLUSTER_REPO/Docs/Gateway/$doc" "$STAGING/docs/"
done
for doc in Magnetar.md ClusterManager.md; do
    [[ -f "$CLUSTER_REPO/Docs/$doc" ]] && cp -a "$CLUSTER_REPO/Docs/$doc" "$STAGING/docs/"
done
# A ready-made two-partition test world so the cluster can be exercised without game scenarios.
cp -a "$CLUSTER_REPO/Test/Saves/Cluster-Simple" "$STAGING/examples/Cluster-Simple"

echo "== Verifying no game binaries are staged"
# Anything that only exists in a game install: the VRage/Sandbox/SpaceEngineers assemblies, the
# Havok wrapper, Keen's ProtoBuf build and the native game libraries. Redistributables that ship
# on purpose (Steamworks.NET + libsteam_api.so from linux-dependencies, Roslyn in Magnetar's
# compiler, Harmony) are not game binaries.
staged_game="$(find "$STAGING" -type f \( -name 'VRage*.dll' -o -name 'Sandbox*.dll' -o -name 'SpaceEngineers*.dll' \
    -o -name 'HavokWrapper.dll' -o -name 'ProtoBuf.Net*.dll' -o -name 'Havok.dll' -o -name 'libHavok.so' \
    -o -name 'libVRageNative.so' -o -name 'libRecastDetour.so' \) -print -quit)"
[[ -z "$staged_game" ]] || { echo "game binary staged: $staged_game" >&2; exit 1; }
for required in magnetar/MagnetarInterim.bin magnetar/Libraries/MagnetarInterim/PluginSdk.dll \
        magnetar/Libraries/MagnetarInterim/Steamworks.NET.dll magnetar/Libraries/MagnetarInterim/libsteam_api.so \
        plugins/cluster/ClusterNode.xml plugins/cluster/WorldAuthority.xml \
        registry/ClusterRegistry.dll tools/MagnetarWorld/MagnetarWorld.dll server/MagnetarWorld.dll cli/cluster.py cli/gateway_supervisor.py cli/magnetar_config.py cli/cluster_hostd.py cli/cluster README.md; do
    [[ -e "$STAGING/$required" ]] || { echo "missing from staging: $required" >&2; exit 1; }
done
python3 -m py_compile "$STAGING/cli/cluster.py" "$STAGING/cli/gateway_supervisor.py" "$STAGING/cli/magnetar_config.py" "$STAGING/cli/cluster_hostd.py"

echo "== Manifest"
SERVER_PIN="$(hub_pin https://raw.githubusercontent.com/CometWorks/magnetar-hub/main/Plugins/DirectTransport.xml)"
CLIENT_PIN="$(hub_pin https://raw.githubusercontent.com/StarCpt/PluginHub/main/Plugins/DirectTransport.xml)"
if [[ "$SERVER_PIN" != "$CLIENT_PIN" ]]; then
    echo "REFUSING: the two direct-transport hub pins disagree, so nodes and clients would run" >&2
    echo "  different transport builds and the archive would name neither:" >&2
    echo "    CometWorks/magnetar-hub (ServerPlugin, nodes+WA): $SERVER_PIN" >&2
    echo "    StarCpt/PluginHub       (ClientPlugin, clients):  $CLIENT_PIN" >&2
    exit 1
fi
echo "   direct-transport from the hub pin: $SERVER_PIN (both hubs agree)"
python3 - "$STAGING" "$VERSION" "$CLUSTER_REPO" "$SERVER_PIN" "$MAGNETAR_HOME" <<'PY'
import json, subprocess, sys, os
staging, version, cluster_repo, transport_commit, magnetar = sys.argv[1:6]
def head(repo):
    return subprocess.run(["git", "-C", repo, "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
def describe(repo):
    return subprocess.run(["git", "-C", repo, "describe", "--tags", "--always", "--dirty"], capture_output=True, text=True).stdout.strip()
magnetar_repo = os.path.expanduser("~/ws/se/se1/magnetar")
json.dump({"name": "magnetar-cluster", "version": version,
           "cluster": head(cluster_repo),
           "direct-transport": transport_commit,   # the hub pin both sides resolve to
           "magnetar": describe(magnetar_repo) if os.path.isdir(os.path.join(magnetar_repo, ".git")) else "unknown"},
          open(f"{staging}/manifest.json", "w"), indent=2)
PY

echo "== Archiving"
ARCHIVE="$OUT_DIR/$NAME.tar.zst"
tar -C "$OUT_DIR" -I 'zstd -19 -T0' -cf "$ARCHIVE" "$NAME"
du -sh "$STAGING" "$ARCHIVE"
echo "OK: $ARCHIVE"
