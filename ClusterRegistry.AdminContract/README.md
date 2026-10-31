# Cluster Gateway admin contract

Implementation-free DTOs for the versioned Cluster Gateway admin API. Gateway,
Quasar, `Quasar.Host`, and the packaged CLI consume this package instead of
referencing Registry implementation types.

The URL prefix and current wire version are exposed by `AdminProtocol`.

Package `0.11.0` adds `PlayerModerationContract.cs`: cluster promotion state with
per-player revisions, compare-and-set role updates, and Gateway admission kick
cooldown observation/clearing. Operations advertise `playerModeration: v1` and
retain HTTP wire v1. See `Docs/Gateway/AdminAPI.md` for ownership and persistence.

Package `0.10.0` adds leased, revisioned, incarnation-bound rotation publications and
client diagnostic DTOs. HTTP wire v1 is retained; the game mod-network codec uses
protocol 4. See `Docs/Gateway/ClientDiagnostics.md`.

Package `0.2.0` adds the recovery-readiness DTOs. This is an additive extension
of wire protocol v1: existing health, status, and plan consumers remain valid.

This branch also adds capability-gated offline partition, compensation cost,
prediction and cache DTOs while retaining wire protocol v1.
