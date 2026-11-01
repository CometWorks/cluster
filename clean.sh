#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

rm -rf "$SCRIPT_DIR/WorldAuthorityPlugin/bin" "$SCRIPT_DIR/WorldAuthorityPlugin/obj"
rm -rf "$SCRIPT_DIR/ClusterNodePlugin/bin" "$SCRIPT_DIR/ClusterNodePlugin/obj"
