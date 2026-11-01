#!/usr/bin/env bash
# Build the cluster release package: one tar.gz holding the Gateway, the MagnetarWorld tool, the
# admin CLI, the cluster CLI, the documentation and the two cluster plugins COMPILED (Viktor,
# 2026-09-15, T-0157): plugins/ClusterNode and plugins/WorldAuthority are Pulsar "local plugins"
# (the DLL, its NuGet dependencies and the manifest XML beside it) that the cluster CLI copies
# into every node's Magnetar config dir. They are never published to the Magnetar hub, so the
# node plugin, the World Authority plugin, the Gateway, the tools and the control scripts of one
# archive are always the same commit. Not included, by design: the Direct Transport plugin (nodes
# and clients both take it from their hubs), Magnetar itself, and every game
# binary (the user supplies a Space Engineers Dedicated Server installation; see
# Build/release-README.md, packaged as the package's README.md).
#
# Usage: Build/release.sh [output-dir]          (default Build/dist)
# Env:   CONFIGURATION       build configuration                          (default Release)
#        DEDICATED64         DedicatedServer64 folder to build against    (default: auto-detect,
#                            see Directory.Build.props)
#        LINUX_DEPENDENCIES  Build/fetch-linux-dependencies.sh destination (default: its default)
#        MAGNETAR_SDK        Magnetar release unpacked by Build/fetch-magnetar.sh, for the plugins'
#                            PluginSdk.dll reference (default: the host install, Directory.Build.props)
#        GATEWAY_RS_DIR      gateway-rs staged by Build/build-gateway-rs.sh (CI builds it in its own
#                            job); unset, this script runs Build/build-gateway-rs.sh itself
#
# The version is <Version> of Version.Build.props; the release workflow builds the same package.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
# Same default as Build/fetch-linux-dependencies.sh; the notices ship beside the binaries.
LINUX_DEPENDENCIES="${LINUX_DEPENDENCIES:-${XDG_CACHE_HOME:-$HOME/.cache}/cluster/linux-dependencies}"
OUT_DIR="$(realpath -m "${1:-$REPO/Build/dist}")"

for tool in dotnet gcc tar git python3 sha256sum; do
    command -v "$tool" >/dev/null || { echo "required tool not found: $tool" >&2; exit 1; }
done

VERSION="$(grep -oPm1 '(?<=<Version>)[^<]+' "$REPO/Version.Build.props")"
[[ -n "$VERSION" ]] || { echo "could not read <Version> from Version.Build.props" >&2; exit 1; }
NAME="cluster-$VERSION"
STAGING="$OUT_DIR/$NAME"
ARCHIVE="$OUT_DIR/ClusterForLinux-$VERSION.tar.gz"

props=("-p:LinuxDependencies=$LINUX_DEPENDENCIES")
[[ -n "${DEDICATED64:-}" ]] && props+=("-p:Dedicated64=$DEDICATED64")
# The plugins compile against Magnetar's PluginSdk.dll: the host install by default
# (Directory.Build.props), or a release unpacked by Build/fetch-magnetar.sh (MAGNETAR_SDK).
[[ -n "${MAGNETAR_SDK:-}" ]] && props+=("-p:Magnetar=$MAGNETAR_SDK")

python3 "$REPO/Build/verify-plugin-protocol.py"

echo "== Building $NAME ($CONFIGURATION)"
# MagnetarPackaging keeps the game's EOSSDK.dll out of the Gateway output; the tool's game
# references are never copied. Both resolve the game assemblies at runtime from SPACE_ENGINEERS_BIN64.
dotnet build "$REPO/ClusterRegistry/ClusterRegistry.csproj" -c "$CONFIGURATION" -p:MagnetarPackaging=true "${props[@]}" --nologo -v q
dotnet build "$REPO/ClusterRegistry.Cli/ClusterRegistry.Cli.csproj" -c "$CONFIGURATION" "${props[@]}" --nologo -v q
dotnet build "$REPO/Tools/MagnetarWorld/MagnetarWorld.csproj" -c "$CONFIGURATION" "${props[@]}" --nologo -v q
# The plugins build against the host's DedicatedServer64 and Magnetar (Directory.Build.props).
# RunPostBuildEvent=Never: the post-build step deploys into a dev Magnetar; a release does not.
# CopyLocalLockFileAssemblies: the NuGet assemblies a local plugin needs beside its DLL.
for plugin in ClusterNodePlugin WorldAuthorityPlugin; do
    dotnet build "$REPO/$plugin/$plugin.csproj" -c "$CONFIGURATION" "${props[@]}" --nologo -v q \
        -p:RunPostBuildEvent=Never -p:CopyLocalLockFileAssemblies=true
done

# The client UI types are also present in the stock DedicatedServer64 reference assemblies.
CLIENT_GAME_DIRECTORY="$(dotnet msbuild "$REPO/ClusterNodePlugin/ClusterNodePlugin.csproj" "${props[@]}" -getProperty:Dedicated64)"
dotnet build "$REPO/ClientDiagnosticsPlugin/ClientDiagnosticsPlugin.csproj" -c "$CONFIGURATION" "${props[@]}" \
    -p:Bin64="$CLIENT_GAME_DIRECTORY" -p:RunPostBuildEvent=Never --nologo -v q

REGISTRY_OUT="$REPO/ClusterRegistry/bin/$CONFIGURATION/net10.0"
CLI_OUT="$REPO/ClusterRegistry.Cli/bin/$CONFIGURATION/net10.0"
TOOL_OUT="$REPO/Tools/MagnetarWorld/bin/$CONFIGURATION/net10.0"
NODE_OUT="$REPO/ClusterNodePlugin/bin/$CONFIGURATION/net10.0"
WA_OUT="$REPO/WorldAuthorityPlugin/bin/$CONFIGURATION/net10.0"
# Ship the runtime NuGet dependencies beside each local plugin. Loading Direct Transport's
# assembly does not eagerly resolve LiteNetLib: plugin type discovery can need it first.
# Assembly.LoadFrom uses the default context, so matching LiteNetLib identities still share
# one assembly with Direct Transport. Harmony and Cecil are provided by Magnetar.
PLUGIN_DEPENDENCIES=(K4os.Compression.LZ4.dll ZstdSharp.dll System.IO.Hashing.dll LiteNetLib.dll)
COMMIT="$(git -C "$REPO" rev-parse HEAD)"

# gateway-rs (T-0229): the Rust data plane, used when the Gateway runs with --relay external.
if [[ -z "${GATEWAY_RS_DIR:-}" ]]; then
    GATEWAY_RS_DIR="$OUT_DIR/gateway-rs-build"
    "$REPO/Build/build-gateway-rs.sh" "$GATEWAY_RS_DIR"
fi
[[ -x "$GATEWAY_RS_DIR/gateway-rs" ]] || { echo "no gateway-rs binary in $GATEWAY_RS_DIR" >&2; exit 1; }

echo "== Staging $STAGING"
rm -rf "$STAGING"
mkdir -p "$STAGING/gateway-rs" "$STAGING/registry" "$STAGING/tools/MagnetarWorld" "$STAGING/cli/ClusterRegistry.Cli" "$STAGING/docs" "$STAGING/examples"
cp -a "$REGISTRY_OUT/." "$STAGING/registry/"
cp -a "$GATEWAY_RS_DIR/." "$STAGING/gateway-rs/"
# Third-party notices for the redistributed natives, from the linux-dependencies archives.
mkdir -p "$STAGING/registry/LICENSES"
cp -a "$LINUX_DEPENDENCIES/steam/LICENSES/Steamworks.NET-LICENSE.txt" "$LINUX_DEPENDENCIES/steam/LICENSES/Steam-NOTICE.txt" \
      "$LINUX_DEPENDENCIES/se1/LICENSES/EOS-NOTICE.txt" "$STAGING/registry/LICENSES/"
cp -a "$LINUX_DEPENDENCIES/VERSION" "$STAGING/registry/LICENSES/linux-dependencies-VERSION"
cp -a "$TOOL_OUT/." "$STAGING/tools/MagnetarWorld/"
cp -a "$CLI_OUT/." "$STAGING/cli/ClusterRegistry.Cli/"
# Local plugins, Pulsar layout: <dir>/<dir>.dll with <dir>.xml beside it (the manifest, whose
# <Id>, dependencies and runtime the loader reads; its SourceDirectories/NuGetReferences are
# ignored for a compiled plugin). <Commit> names the release commit.
stage_plugin() {  # stage_plugin NAME OUT_DIR MANIFEST
    local name="$1" out="$2" manifest="$3" dependency
    mkdir -p "$STAGING/plugins/$name"
    cp -a "$out/$name.dll" "$STAGING/plugins/$name/"
    for dependency in "${PLUGIN_DEPENDENCIES[@]}"; do
        [[ -f "$out/$dependency" ]] || { echo "plugin dependency not built: $out/$dependency" >&2; exit 1; }
        cp -a "$out/$dependency" "$STAGING/plugins/$name/"
    done
    sed "s|<Commit>[^<]*</Commit>|<Commit>$COMMIT</Commit>|" "$REPO/$manifest" > "$STAGING/plugins/$name/$name.xml"
}
stage_plugin ClusterNode "$NODE_OUT" ClusterNode.xml
stage_plugin WorldAuthority "$WA_OUT" WorldAuthority.xml
mkdir -p "$STAGING/client/ClusterDiagnostics"
cp "$REPO/ClientDiagnosticsPlugin/bin/$CONFIGURATION/net10.0/ClusterDiagnostics.dll" "$STAGING/client/ClusterDiagnostics/plugin.dll"
sed "s|<Commit>[^<]*</Commit>|<Commit>$COMMIT</Commit>|" "$REPO/ClusterDiagnosticsClient.xml" > "$STAGING/client/ClusterDiagnostics/plugin.xml"

cp -a "$REPO/Cli/cluster.py" "$REPO/Cli/gateway_supervisor.py" "$REPO/Cli/magnetar_config.py" "$REPO/Cli/cluster_hostd.py" "$REPO/Cli/managed_deployment.py" "$REPO/Cli/deployment-capabilities.json" "$REPO/Cli/cluster" "$REPO/Cli/cluster-status" "$REPO/Cli/gateway-admin" "$STAGING/cli/"
SDK_DIRECTORY="$(dotnet msbuild "$REPO/ClusterNodePlugin/ClusterNodePlugin.csproj" "${props[@]}" -getProperty:MagnetarBinDir)"
GAME_DIRECTORY="$(dotnet msbuild "$REPO/ClusterNodePlugin/ClusterNodePlugin.csproj" "${props[@]}" -getProperty:Dedicated64)"
dotnet build "$REPO/Test/PackagedPluginCheck/PackagedPluginCheck.csproj" -c "$CONFIGURATION" --nologo -v q
for plugin in ClusterNode WorldAuthority; do
    # Separate processes ensure one role cannot accidentally supply another role's dependencies.
    dotnet "$REPO/Test/PackagedPluginCheck/bin/$CONFIGURATION/net10.0/PackagedPluginCheck.dll" \
        "$STAGING/plugins/$plugin/$plugin.dll" "$GAME_DIRECTORY" "$SDK_DIRECTORY"
done
dotnet "$REGISTRY_OUT/ClusterRegistry.dll" --check-plugin-sdk "${SDK_DIRECTORY}PluginSdk.dll"
# Quasar accepts a Magnetar whose PluginSdk contract has this major and at least this minor. The
# exact-file hash is its fallback when either side declares no contract (Magnetar before the contract,
# Quasar before the contract check).
SDK_CONTRACT="$(dotnet "$REGISTRY_OUT/ClusterRegistry.dll" --plugin-sdk-contract "${SDK_DIRECTORY}PluginSdk.dll")"
python3 - "$STAGING/cli/deployment-capabilities.json" "${SDK_DIRECTORY}PluginSdk.dll" "$SDK_CONTRACT" <<'SDKPIN'
import hashlib, json, pathlib, sys
path = pathlib.Path(sys.argv[1])
data = json.loads(path.read_text())
data["pluginSdkSha256"] = hashlib.sha256(pathlib.Path(sys.argv[2]).read_bytes()).hexdigest()
if sys.argv[3]:
    data["pluginSdkContract"] = sys.argv[3]
path.write_text(json.dumps(data, separators=(",", ":")) + "\n")
SDKPIN
chmod +x "$STAGING/cli/cluster" "$STAGING/cli/cluster-status" "$STAGING/cli/gateway-admin"
# Documentation: how to run a cluster, the REST APIs of the Gateway and the node plugins, the
# CLI tools shipped here, and the environment variables everything reads.
for doc in ClientDiagnostics.md ClusterCLI.md AdminAPI.md GatewayAPI.md NodeAPI.md EnvVars.md ManagedDeployment.md PluginServices.md NodeScaling.md NodeControl.md; do
    cp -a "$REPO/Docs/Gateway/$doc" "$STAGING/docs/"
done
cp -a "$REPO/Docs/Magnetar.md" "$REPO/Docs/ClusterManager.md" "$STAGING/docs/"
cp -a "$REPO/ClusterRegistry/README.md" "$STAGING/docs/Registry.md"
cp -a "$REPO/ClusterRegistry.AdminContract/README.md" "$STAGING/docs/AdminContract.md"
cp -a "$REPO/Build/release-README.md" "$STAGING/README.md"
cp -a "$REPO/LICENSE" "$STAGING/LICENSE"
# A ready-made two-partition test world so the cluster can be exercised without game scenarios.
cp -a "$REPO/Test/Saves/Cluster-Simple" "$STAGING/examples/Cluster-Simple"
# Archive exact release symbols before trimming the runtime package.
SYMBOLS="$OUT_DIR/ClusterSymbols-$VERSION"
rm -rf "$SYMBOLS"
mkdir -p "$SYMBOLS"
printf '%s\n' "$COMMIT" > "$SYMBOLS/COMMIT"
printf '%s\n' "$VERSION" > "$SYMBOLS/VERSION"
python3 - "$STAGING" "$SYMBOLS" "$NODE_OUT" "$WA_OUT" <<'SYMBOLS_PY'
from pathlib import Path
import hashlib, json, shutil, sys
source, target = map(Path, sys.argv[1:3])
for path in list(source.rglob('*.pdb')) + list(source.rglob('*.debug')):
    relative = path.relative_to(source)
    destination = target / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.move(path, destination)
for plugin_output in sys.argv[3:]:
    for path in Path(plugin_output).glob('*.pdb'):
        destination = target / 'plugins' / path.name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, destination)
files = {str(p.relative_to(target)): hashlib.sha256(p.read_bytes()).hexdigest() for p in target.rglob('*') if p.is_file()}
binaries = {str(p.relative_to(source)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in source.rglob('*') if p.is_file() and (p.suffix in ('.dll', '.so') or p.name == 'gateway-rs')}
(target / 'symbols.json').write_text(json.dumps({'symbols': files, 'binaries': binaries}, indent=2))
SYMBOLS_PY
python3 "$REPO/Build/verify-symbols.py" "$STAGING" "$SYMBOLS"
tar -C "$OUT_DIR" -czf "$OUT_DIR/ClusterSymbols-$VERSION.tar.gz" "ClusterSymbols-$VERSION"

echo "== Verifying"
# Anything that only exists in a game install must not be staged: the VRage/Sandbox/SpaceEngineers
# assemblies, the EOS managed binding, Keen's ProtoBuf build and the native game libraries.
# Steamworks.NET, libsteam_api.so and the Linux EOS SDK are redistributables from
# linux-dependencies and ship on purpose.
staged_game="$(find "$STAGING" -type f \( -name 'VRage*.dll' -o -name 'Sandbox*.dll' -o -name 'SpaceEngineers*.dll' \
    -o -name 'EOSSDK.dll' -o -name 'HavokWrapper.dll' -o -name 'ProtoBuf.Net*.dll' -o -name 'Havok.dll' \
    -o -name 'libHavok.so' -o -name 'libVRageNative.so' -o -name 'libRecastDetour.so' \) -print -quit)"
[[ -z "$staged_game" ]] || { echo "game binary staged: $staged_game" >&2; exit 1; }
for required in gateway-rs/gateway-rs gateway-rs/libsteam_api.so gateway-rs/LICENSES/THIRD-PARTY.txt registry/ClusterRegistry registry/ClusterRegistry.dll registry/Steamworks.NET.dll registry/libsteam_api.so registry/libsteam_api64.so \
        registry/libEOSSDK-Linux-Shipping.so registry/EOSSDK-Shipping.dll \
        registry/LICENSES/Steamworks.NET-LICENSE.txt registry/LICENSES/Steam-NOTICE.txt registry/LICENSES/EOS-NOTICE.txt \
        tools/MagnetarWorld/MagnetarWorld.dll cli/ClusterRegistry.Cli/ClusterRegistry.Cli.dll \
        plugins/ClusterNode/ClusterNode.dll plugins/ClusterNode/ClusterNode.xml \
        plugins/WorldAuthority/WorldAuthority.dll plugins/WorldAuthority/WorldAuthority.xml \
        cli/cluster cli/cluster-status cli/cluster.py cli/gateway_supervisor.py cli/magnetar_config.py cli/cluster_hostd.py cli/managed_deployment.py cli/gateway-admin \
        docs/ClusterCLI.md docs/AdminAPI.md docs/GatewayAPI.md docs/NodeAPI.md docs/EnvVars.md docs/NodeControl.md \
        examples/Cluster-Simple/Sandbox.sbc README.md LICENSE; do
    [[ -e "$STAGING/$required" ]] || { echo "missing from staging: $required" >&2; exit 1; }
done
python3 -m py_compile "$STAGING/cli/cluster.py" "$STAGING/cli/gateway_supervisor.py" "$STAGING/cli/magnetar_config.py" "$STAGING/cli/cluster_hostd.py" "$STAGING/cli/managed_deployment.py"
python3 -c 'import sys; compile(open(sys.argv[1]).read(), sys.argv[1], "exec")' "$STAGING/cli/cluster-status"
find "$STAGING" -name __pycache__ -type d -exec rm -rf {} +
# The self-tests need no game install: the Gateway's parser, registry and admin API sections,
# and the admin CLI's parity sweep against the admin contract. GATEWAY_RS_BIN runs the
# multiplexing scenario a second time through the staged gateway-rs (--relay external).
for selftest in registry/ClusterRegistry.dll cli/ClusterRegistry.Cli/ClusterRegistry.Cli.dll; do
    log="$OUT_DIR/self-test-$(basename "$selftest" .dll).log"
    (cd "$STAGING" && GATEWAY_RS_BIN="$STAGING/gateway-rs/gateway-rs" dotnet "$selftest" --self-test) > "$log" 2>&1 \
        || { cat "$log" >&2; echo "self-test failed: $selftest" >&2; exit 1; }
done

echo "== Manifest"
python3 - "$STAGING" "$VERSION" "$REPO" <<'PY'
import json, subprocess, sys
from datetime import datetime, timezone
staging, version, repo = sys.argv[1:4]
def git(*args):
    return subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True).stdout.strip()
json.dump({"name": "cluster", "version": version, "commit": git("rev-parse", "HEAD"),
           "describe": git("describe", "--tags", "--always", "--dirty"),
           "built": datetime.now(timezone.utc).isoformat(timespec="seconds")},
          open(f"{staging}/manifest.json", "w"), indent=2)
PY

echo "== Archiving"
tar -C "$OUT_DIR" -czf "$ARCHIVE" "$NAME"
(cd "$OUT_DIR" && sha256sum "$(basename "$ARCHIVE")" "ClusterSymbols-$VERSION.tar.gz" > SHA256SUMS)
du -sh "$STAGING" "$ARCHIVE"
cat "$OUT_DIR/SHA256SUMS"
echo "OK: $ARCHIVE"
