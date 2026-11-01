#!/usr/bin/env bash
# Interop tests: the Rust `lnl` crate against C# LiteNetLib 1.3.1, in both roles.
#
#   GatewayRs/interop/run.sh            # all scenarios
#   GatewayRs/interop/run.sh lossy      # scenarios whose name contains "lossy"
#
# Needs cargo (rust-toolchain.toml) and the .NET 10 SDK (LiteNetLib 1.3.1 from NuGet).
# Uses loopback UDP ports 40100-40199 only. Logs: GatewayRs/target/interop-logs/.
set -u -o pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
filter=${1:-}
logs="$root/target/interop-logs"
mkdir -p "$logs"

echo "building lnl-interop and the C# peer..."
(cd "$root" && cargo build --release --locked -q -p lnl-interop) || exit 1
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
    dotnet build "$here/dotnet/LnlInterop.csproj" -c Release -o "$root/target/interop-dotnet" -nologo -v q >"$logs/dotnet-build.log" 2>&1 \
    || { cat "$logs/dotnet-build.log"; exit 1; }

RS="$root/target/release/lnl-interop"
CS="dotnet $root/target/interop-dotnet/LnlInterop.dll"

passed=0
failed=0
failures=()
port=40100

# scenario NAME SERVER_IMPL "SERVER_ARGS" CLIENT_IMPL "CLIENT_ARGS" ["PROXY_ARGS"]
# IMPL is rs or cs. With PROXY_ARGS the client connects through the impairing proxy.
scenario() {
    local name=$1 simpl=$2 sargs=$3 cimpl=$4 cargs=$5 pargs=${6:-}
    if [[ -n "$filter" && "$name" != *"$filter"* ]]; then return; fi
    local sport=$port cport=$port
    port=$((port + 2))
    local sbin cbin
    [[ $simpl == rs ]] && sbin=$RS || sbin=$CS
    [[ $cimpl == rs ]] && cbin=$RS || cbin=$CS
    local slog="$logs/$name.server.log" clog="$logs/$name.client.log" plog="$logs/$name.proxy.log"
    # shellcheck disable=SC2086
    $sbin server --bind 127.0.0.1:$sport $sargs >"$slog" 2>&1 &
    local spid=$!
    local ppid=""
    sleep 0.4
    if [[ -n "$pargs" ]]; then
        cport=$((sport + 1))
        # shellcheck disable=SC2086
        "$RS" proxy --listen 127.0.0.1:$cport --target 127.0.0.1:$sport $pargs >"$plog" 2>&1 &
        ppid=$!
        sleep 0.2
    fi
    # shellcheck disable=SC2086
    timeout 120 $cbin client --connect 127.0.0.1:$cport $cargs >"$clog" 2>&1
    local cstatus=$?
    local swait=0
    while kill -0 "$spid" 2>/dev/null && ((swait < 150)); do sleep 0.1; swait=$((swait + 1)); done
    if kill -0 "$spid" 2>/dev/null; then kill "$spid" 2>/dev/null; fi
    wait "$spid"
    local sstatus=$?
    if [[ -n "$ppid" ]]; then kill "$ppid" 2>/dev/null; wait "$ppid" 2>/dev/null; fi
    if ((sstatus == 0 && cstatus == 0)); then
        passed=$((passed + 1))
        printf 'PASS %-34s | %s | %s\n' "$name" "$(grep -h '^PASS' "$slog" | cut -c1-70)" "$(grep -h '^PASS' "$clog" | cut -c1-70)"
    else
        failed=$((failed + 1))
        failures+=("$name")
        printf 'FAIL %-34s (server %s, client %s)\n' "$name" "$sstatus" "$cstatus"
        tail -n 5 "$slog" "$clog" | sed 's/^/    /'
    fi
}

common="--expect-unreliable 40"

# Delivery methods, fragmentation and graceful disconnects, each side closing.
scenario cs-client_rs-server_client-closes rs "--end await-close $common" cs "--end close $common"
scenario cs-client_rs-server_server-closes rs "--end close $common" cs "--end await-close $common"
scenario rs-client_cs-server_client-closes cs "--end await-close $common" rs "--end close $common"
scenario rs-client_cs-server_server-closes cs "--end close $common" rs "--end await-close $common"
scenario rs-client_rs-server rs "--end await-close $common" rs "--end close $common"

# Timeouts: one side vanishes without a Disconnect, the other must report Timeout.
scenario timeout_cs-client-dies_rs-server rs "--end await-timeout --timeout-ms 3000" cs "--end die --timeout-ms 3000"
scenario timeout_rs-server-dies_cs-client rs "--end die --timeout-ms 3000" cs "--end await-timeout --timeout-ms 3000"
scenario timeout_cs-server-dies_rs-client cs "--end die --timeout-ms 3000" rs "--end await-timeout --timeout-ms 3000"

# MTU discovery up to 1432 on loopback, both directions.
scenario mtu_cs-client_rs-server rs "--mtu-discovery --expect-mtu 1432 --end await-close" cs "--mtu-discovery --expect-mtu 1432 --end close"
scenario mtu_rs-client_cs-server cs "--mtu-discovery --expect-mtu 1432 --end await-close" rs "--mtu-discovery --expect-mtu 1432 --end close"

# A held (unanswered) ConnectionRequest, accepted later while the client keeps retrying.
scenario hold_cs-client_rs-server rs "--hold-ms 2500 --end await-close" cs "--end close"
scenario hold_rs-client_cs-server cs "--hold-ms 2500 --end await-close" rs "--end close"

# Reject with data.
scenario reject_cs-client_rs-server rs "--reject" cs "--expect-reject"
scenario reject_rs-client_cs-server cs "--reject" rs "--expect-reject"

# Loss, duplication, delay and reordering (jitter) across the interop boundary. RTT >= 2 x 15 ms.
# No upper RTT bound: LiteNetLib counts a lost ping as a full ping interval of RTT.
lossy="--loss 0.05 --dup 0.03 --delay-ms 15 --jitter-ms 25 --seed"
scenario lossy_cs-client_rs-server rs "--end await-close --timeout-ms 8000 --min-rtt 30" cs "--end close --timeout-ms 8000 --min-rtt 30 --grace-ms 5000" "$lossy 11"
scenario lossy_rs-client_cs-server cs "--end close --timeout-ms 8000 --min-rtt 30 --grace-ms 5000" rs "--end await-close --timeout-ms 8000 --min-rtt 30" "$lossy 12"
scenario lossy-heavy_cs-client_rs-server rs "--end close --timeout-ms 10000 --deadline-ms 90000 --grace-ms 8000" cs "--end await-close --timeout-ms 10000 --deadline-ms 90000" "--loss 0.2 --dup 0.1 --delay-ms 5 --jitter-ms 40 --seed 13"
scenario lossy-heavy_rs-client_cs-server cs "--end await-close --timeout-ms 10000 --deadline-ms 90000" rs "--end close --timeout-ms 10000 --deadline-ms 90000 --grace-ms 8000" "--loss 0.2 --dup 0.1 --delay-ms 5 --jitter-ms 40 --seed 14"

# Peer address change: the proxy moves the client to a new source port mid-run; the server (with
# AllowPeerAddressChange) answers PeerNotFound, the client sends its network-changed packet and the
# session continues on the new address.
scenario move_cs-client_rs-server rs "--allow-address-change --expect-address-change --end await-close" cs "--end close" "--rebind-after-ms 300"
scenario move_rs-client_cs-server cs "--allow-address-change --expect-address-change --end await-close" rs "--end close" "--rebind-after-ms 300"

echo
echo "interop: $passed passed, $failed failed"
if ((failed > 0)); then
    printf '  failed: %s\n' "${failures[@]}"
    exit 1
fi
