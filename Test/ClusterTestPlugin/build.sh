#!/usr/bin/env bash
# Build the test utility plugin against a release package and lay it out as local plugins:
#   Test/ClusterTestPlugin/build.sh PACKAGE OUT
# writes OUT/ClusterTestNode/{ClusterTestNode.dll,.xml} and OUT/ClusterTestWA/{...}, the folders
# CLUSTER_EXTRA_PLUGINS=node:OUT/ClusterTestNode,wa:OUT/ClusterTestWA installs. MAGNETAR_HOME, the
# bench's Magnetar override (Test/hostenv.py), also picks the PluginSdk the plugin compiles against.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE="$(realpath "${1:?usage: build.sh PACKAGE OUT}")"
OUT="$(realpath -m "${2:?usage: build.sh PACKAGE OUT}")"
COMMIT="$(git -C "$HERE" rev-parse HEAD 2>/dev/null || echo unknown)"
for name in Node WA; do
    project="ClusterTest$name"
    dotnet build "$HERE/$name/$project.csproj" -c Release --nologo -v q \
        -p:ClusterPlugins="$PACKAGE/plugins" ${MAGNETAR_HOME:+-p:Magnetar="$MAGNETAR_HOME"} \
        -o "$OUT/.build/$project" >&2
    mkdir -p "$OUT/$project"
    cp -a "$OUT/.build/$project/$project.dll" "$OUT/$project/"
    sed "s|<Commit>[^<]*</Commit>|<Commit>$COMMIT</Commit>|" "$HERE/$name/$project.xml" > "$OUT/$project/$project.xml"
done
rm -rf "$OUT/.build"
echo "$OUT"
