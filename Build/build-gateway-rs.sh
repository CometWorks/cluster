#!/usr/bin/env bash
# Build and stage gateway-rs, the Rust data plane of the Gateway (T-0229, Plan/RustGateway.md §7):
#
#   <out>/gateway-rs                the binary (links glibc, libgcc_s and, by an $ORIGIN rpath,
#                                   the libsteam_api.so beside it)
#   <out>/libsteam_api.so           Steamworks SDK redistributable, bundled by steamworks-sys
#   <out>/LICENSES/THIRD-PARTY.txt  every crate compiled in, with its licence expression
#   <out>/VERSION                   crate version and the commit it was built from
#
# Usage: Build/build-gateway-rs.sh [out-dir]      (default Build/dist/gateway-rs)
# Env:   GATEWAY_RS_CHECKS=0   skip clippy and the tests (default 1: clippy -D warnings, cargo test)
#
# The toolchain is pinned by GatewayRs/rust-toolchain.toml; Cargo.lock is committed and used
# with --locked. ClusterRegistry's build does not depend on this one: Build/release.sh takes the
# staged directory from GATEWAY_RS_DIR (CI builds it in its own job) or runs this script.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORKSPACE="$REPO/GatewayRs"
OUT_DIR="$(realpath -m "${1:-$REPO/Build/dist/gateway-rs}")"
CHECKS="${GATEWAY_RS_CHECKS:-1}"

# rustup installs into ~/.cargo/bin, which a non-login shell (a deploy script) may not have.
command -v cargo >/dev/null || PATH="$HOME/.cargo/bin:$PATH"
command -v cargo >/dev/null || { echo "cargo not found (install rustup; the toolchain is pinned in GatewayRs/rust-toolchain.toml)" >&2; exit 1; }

cd "$WORKSPACE"
if [[ "$CHECKS" != 0 ]]; then
    echo "== gateway-rs: clippy and tests"
    cargo clippy --locked --all-targets -q -- -D warnings
    cargo test --locked -q
fi
echo "== gateway-rs: release build"
cargo build --locked --release -q -p gateway-rs

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR/LICENSES"
cp -a "$WORKSPACE/target/release/gateway-rs" "$OUT_DIR/gateway-rs"
# The build ID stays in both files; gdb also finds the separate file through .gnu_debuglink.
objcopy --only-keep-debug "$OUT_DIR/gateway-rs" "$OUT_DIR/gateway-rs.debug"
strip --strip-debug "$OUT_DIR/gateway-rs"
objcopy --add-gnu-debuglink="$OUT_DIR/gateway-rs.debug" "$OUT_DIR/gateway-rs"
readelf -n "$OUT_DIR/gateway-rs" > "$OUT_DIR/BUILD-ID.txt"
(cd "$OUT_DIR" && sha256sum gateway-rs gateway-rs.debug > SYMBOL-SHA256SUMS)
# gateway-rs/build.rs staged it next to the binary.
cp -a "$WORKSPACE/target/release/libsteam_api.so" "$OUT_DIR/libsteam_api.so"
metadata="$(mktemp)"
trap 'rm -f "$metadata"' EXIT
cargo metadata --locked --format-version 1 --filter-platform "$(rustc -vV | sed -n 's/^host: //p')" > "$metadata"
{
    python3 "$REPO/Build/gateway-rs-licences.py" "$metadata"
    printf '\nlibsteam_api.so: Steamworks SDK redistributable (Valve), as shipped in steamworks-sys; the same\n'
    printf 'library the Registry ships in registry/ for its in-process Steam frontend.\n'
} > "$OUT_DIR/LICENSES/THIRD-PARTY.txt"
printf 'gateway-rs %s\ncommit %s\n' "$(sed -n 's/^version = "\(.*\)"/\1/p' "$WORKSPACE/gateway-rs/Cargo.toml" | head -1)" \
    "$(git -C "$REPO" rev-parse HEAD)" > "$OUT_DIR/VERSION"
printf 'toolchain %s\nsource-dirty %s\n' "$(rustc --version)" \
    "$(test -z "$(git -C "$REPO" status --porcelain)" && echo false || echo true)" >> "$OUT_DIR/VERSION"
python3 "$REPO/Build/verify-symbols.py" --gateway "$OUT_DIR"
"$OUT_DIR/gateway-rs" --version
echo "OK: $OUT_DIR"
