#!/usr/bin/env bash
# Download the Linux natives the gateway build links against from the latest
# CometWorks/linux-dependencies release: the Steam pair (Steamworks.NET.dll + libsteam_api.so,
# steam-dependencies.tar.gz) into $LINUX_DEPENDENCIES/steam and the SE1 set (for
# libEOSSDK-Linux-Shipping.so, se1-dependencies.tar.gz) into $LINUX_DEPENDENCIES/se1.
#
# Usage: Build/fetch-linux-dependencies.sh
# Env:   LINUX_DEPENDENCIES  destination (default ~/.cache/cluster/linux-dependencies, which is
#                            also where Directory.Build.props looks without an override)
set -euo pipefail

DEST="${LINUX_DEPENDENCIES:-${XDG_CACHE_HOME:-$HOME/.cache}/cluster/linux-dependencies}"
REPO_URL="https://github.com/CometWorks/linux-dependencies"

# The latest/download URL redirects to releases/download/<tag>/<asset> (no API call, no rate
# limit); that first hop names the release, the download follows the rest of the redirects.
TAG="$(curl -fsSI -o /dev/null -w '%{redirect_url}' "$REPO_URL/releases/latest/download/steam-dependencies.tar.gz" \
       | sed -E 's|.*/releases/download/([^/]+)/.*|\1|')"
[[ -n "$TAG" && "$TAG" != http* ]] || { echo "could not resolve the latest linux-dependencies release" >&2; exit 1; }
echo "== linux-dependencies $TAG"

fetch() {
    local asset="$1" subdir="$2"
    mkdir -p "$DEST/$subdir"
    curl -fsSL -o "$DEST/$asset" "$REPO_URL/releases/download/$TAG/$asset"
    tar -xzf "$DEST/$asset" -C "$DEST/$subdir"
    echo "   $asset -> $DEST/$subdir"
}

fetch steam-dependencies.tar.gz steam
fetch se1-dependencies.tar.gz se1
test -f "$DEST/steam/Steamworks.NET.dll"
test -f "$DEST/steam/libsteam_api.so"
test -f "$DEST/se1/libEOSSDK-Linux-Shipping.so"
echo "$TAG" > "$DEST/VERSION"
echo "OK: $DEST"
