#!/usr/bin/env bash
# Download the Magnetar launcher release the plugin builds compile against (T-0157): the node and
# World Authority plugins reference Magnetar's PluginSdk.dll (Directory.Build.props resolves it
# under <Magnetar>/Libraries/MagnetarInterim/), so a host without a Magnetar install - the GitHub
# release runner - needs one unpacked somewhere. The package itself never ships Magnetar.
#
# Usage: Build/fetch-magnetar.sh [TAG]      (default: the required v2.4.3.9 SDK release)
# PR-only: Build/fetch-magnetar.sh --source CHECKOUT (requires DEDICATED64).
# Env:   MAGNETAR_SDK   destination (default ~/.cache/cluster/magnetar); pass the same path to
#                       Build/release.sh, which hands it to msbuild as -p:Magnetar=...
#        GH_TOKEN       optional; raises the GitHub API rate limit (the repository is public)
set -euo pipefail
DEST="${MAGNETAR_SDK:-${XDG_CACHE_HOME:-$HOME/.cache}/cluster/magnetar}"
REPO="CometWorks/magnetar"
# Review packages may use an explicitly pinned SDK checkout before its release exists.
# The release workflow never uses this path for main or manual publication.
if [[ "${1:-}" == "--source" ]]; then
    [[ $# == 2 ]] || { echo "usage: $0 --source CHECKOUT" >&2; exit 1; }
    SOURCE="$(realpath "$2")"
    EXPECTED="$(cat "$(dirname "$0")/pr-magnetar-sdk-revision.txt")"
    ACTUAL="$(git -C "$SOURCE" rev-parse HEAD)"
    [[ "$ACTUAL" == "$EXPECTED" ]] || { echo "SDK checkout does not match the pinned PR revision" >&2; exit 1; }
    : "${DEDICATED64:?DEDICATED64 is required when building the review SDK}"
    python3 "$(dirname "$0")/verify-plugin-protocol.py" "$SOURCE"
    BUILD="$(mktemp -d)"
    trap 'rm -rf "$BUILD"' EXIT
    dotnet build "$SOURCE/PluginSdk/PluginSdk.csproj" -c Release -p:DS64="$DEDICATED64" \
        --output "$BUILD" --nologo -v q
    mkdir -p "$DEST/Libraries/MagnetarInterim"
    cp "$BUILD/PluginSdk.dll" "$DEST/Libraries/MagnetarInterim/PluginSdk.dll"
    echo "source:$ACTUAL" > "$DEST/VERSION"
    echo "OK: $DEST (review SDK source $ACTUAL; not a released Magnetar installation)"
    exit 0
fi
command -v 7z >/dev/null || { echo "required tool not found: 7z (p7zip-full)" >&2; exit 1; }

auth=()
[[ -n "${GH_TOKEN:-}" ]] && auth=(-H "Authorization: Bearer $GH_TOKEN")
release="https://api.github.com/repos/$REPO/releases/tags/${1:-v2.4.3.9}"
# One API call names the release and its Linux asset (MagnetarForLinux-<version>.7z); the
# download itself is a plain redirect and counts against no limit.
read -r TAG ASSET < <(curl -fsSL "${auth[@]}" -H "Accept: application/vnd.github+json" "$release" \
    | python3 -c 'import json,sys
d = json.load(sys.stdin)
assets = [a["name"] for a in d.get("assets", []) if a["name"].startswith("MagnetarForLinux-") and a["name"].endswith(".7z")]
if not assets: sys.exit("no MagnetarForLinux-*.7z asset on release " + str(d.get("tag_name")))
print(d["tag_name"], assets[0])')
echo "== magnetar $TAG ($ASSET)"

if [[ -f "$DEST/VERSION" && "$(cat "$DEST/VERSION")" == "$TAG" && -f "$DEST/Libraries/MagnetarInterim/PluginSdk.dll" ]]; then
    echo "OK: $DEST (already at $TAG)"
    exit 0
fi
rm -rf "$DEST"
mkdir -p "$DEST"
curl -fsSL -o "$DEST/$ASSET" "https://github.com/$REPO/releases/download/$TAG/$ASSET"
7z x -y -o"$DEST" "$DEST/$ASSET" >/dev/null
rm -f "$DEST/$ASSET"
# The archive holds one top-level Magnetar/ folder (v2.4.2.0); the install layout Directory.Build.props
# expects (<Magnetar>/Libraries/MagnetarInterim/PluginSdk.dll) is what is inside it.
if [[ ! -f "$DEST/Libraries/MagnetarInterim/PluginSdk.dll" && -d "$DEST/Magnetar/Libraries" ]]; then
    mv "$DEST/Magnetar"/* "$DEST/"
    rmdir "$DEST/Magnetar"
fi
test -f "$DEST/Libraries/MagnetarInterim/PluginSdk.dll" || { echo "no Libraries/MagnetarInterim/PluginSdk.dll in $ASSET" >&2; exit 1; }
echo "$TAG" > "$DEST/VERSION"
echo "OK: $DEST"
