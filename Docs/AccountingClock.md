# Accounting time and durable sun history

Accounting contract 3 separates resource work from the existing replication, catalog,
liveness and synchronized-sky clocks. Those clocks retain their existing behavior.

| Interval | Address source | Persisted profile |
| --- | --- | --- |
| Standalone | Native elapsed world time / 16 ms | Schema 2; known unpaid schema 1 converts on a detached copy |
| Cluster live observation | This process's completed native updates and session epoch | Schema 3, `ClusterLocal` |
| Cluster offline settlement | Durably acknowledged completed WA updates | Schema 3, `ClusterAuthority`, accounting epoch and capture generation |

A completed native update is one resource frame. Resource seconds remain 1/60; native
world elapsed time remains 16 ms per update. The configurable cap still defaults to
72 simulation hours, with 225000 updates per native world hour. Settlement consumes
the **first** capped interval and records the actual wake address, forfeiting excess.
Neither a wall delay, a paused update, a clock correction nor an admin offset adds work.
A stalled former owner does not stop WA-owned offline progress; a stalled WA adds none.

Capture runs on the game thread. Before serializing a fresh unpaid local profile, Cluster
obtains a current WA cut through the existing global query channel. The WA captures on
its game thread and the registry commits the clock and ordered sun runs in one SQLite
transaction before answering. This translates the zero-length unpaid interval into WA
coordinates without changing native stocks, recipes or timer phases. It also translates
the non-admin sun origin while retaining the captured manual offset.

The registry owns the durable accounting epoch and native-work high-water. WA restart
seeds from that acknowledged state and adds newly completed native updates, never wall
downtime. Current-generation acknowledgement is required for admission and settlement.
Wrong world/epoch, backwards work, future generation and missing history refuse settlement
before any group mutates or any entity is constructed. The immutable Final remains the
retry basis. An ordinary paid new-generation Final drops the unpaid profile.

## Ordered geometry path

Every completed WA native update records its actual starting session ticks **minus the
admin offset**. A run stores `(work position, non-admin sun ticks)`. Further native
updates extend it only when their observed value equals the previous run's native
160000-tick progression; any difference starts another run. A stall contributes no work
position. Run order is retained through finite storage and production; a histogram or
interpolation between heartbeat endpoints would not preserve those transactions.

The model reads the actual run at each geometric-bin event. Configurable solar bins,
cached all-pivot geometry, darkness, nameplate limits and the uncertain-shadow-only
allowance retain their existing rules. Manual sun changes remain steps at the interval
end: their offsets are excluded from recorded non-admin progression and are not spread
over the unpaid interval.

Durable runs are append-only in the registry's existing tracked row store. They are not
evicted by a rolling 72-hour window: an older sleeping partition can still need its first
capped interval. At worst there is one two-int64 run per completed native update; ordinary
native stretches share a run. Persistent cost therefore follows actual discontinuities,
not the configured bin count or number of offline groups. Reads use an ordered binary
index and return only the requested range plus its preceding origin. Conservative
retention avoids guessing which old immutable Final remains a recovery entitlement;
long-term compaction is separate from this correctness repair.

The **live-only** node cache is bounded by the existing observation window (including a
full natural rotation for `InPlace`). Old live comparisons refuse when their path is no
longer cached. This cannot evict an unpaid interval: durable freeze always captures fresh
stocks and translates to the independently retained WA history. Qualification reports
record actual run counts, payload/database sizes and memory measurements; shared-host
correctness checks are not exclusive performance measurements.

## Compatibility and predictions

Schema 1/2 contains no discriminator proving that old Cluster addresses came from native
updates rather than initialized wall extrapolation. New Cluster settlement refuses that
ambiguous state without rewriting its profile or Final. The original standalone native
16 ms mapping and detached known schema-1 clock conversion remain supported. Already paid
native state receives no retroactive adjustment. Shared profile schema 3 is independent
of the standalone mechanical record's schema-2 required-root-history rules.

Core and Cluster consumption must be upgraded together through the immutable core pin.
Older consumers refuse contract 3; older models cannot settle schema 3. AdminContract
0.10 and the public configuration surface are unchanged. Dry-run duration and cap units
are unchanged; a rotating environmental prediction requiring an unavailable actual
sun/work path returns the existing skipped-result representation. It never invents a
future path from endpoints. Pure legacy/native-world model inputs keep their documented
detached prediction behavior.
