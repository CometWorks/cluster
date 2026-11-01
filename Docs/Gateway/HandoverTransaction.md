# Is the handover already transactional, and what is missing?

Viktor asked (2026-08-19): can a handover mark the partition "moving"
before it starts and clear it only on arrival, so the state prevents bad
decisions mid-handover - time limited, auto-failing after about a minute?

Answer: **most of it already exists; the time limit does not.** This note
records what is there, what is genuinely missing, and - importantly - the
evidence that the missing half is NOT what is currently going wrong.

## What already exists

`HandoverRecord` (GatewayRegistry.cs) is the transaction:

```
record HandoverRecord(Id, Partition, SourceNode, SourceEpoch,
                      SourceGeneration, TargetNode, TargetEpoch,
                      State, CommittedGeneration) { Reconciled }
enum HandoverState { Begun, Active, Committed, Aborted }
```

* The partition IS marked moving: `Begin` refuses a second handover while
  one is `Begun` or `Active` (GatewayRegistry.cs:1542).
* The state IS consulted to avoid bad decisions: node close waits on
  finalising handovers (line 445), and the automatic loop skips partitions
  in `Begun`/`Active` (line 613).
* Crash recovery exists: `ReconcileHandovers()` probes BOTH nodes with the
  transfer fence and decides Wait/Abort/complete, then discards on the
  target and thaws on the source. It runs at startup and every automatic
  pass.

So "mark it moving, clear it on arrival, and use the state" is done.

## What is missing: the clock

`HandoverRecord` has **no timestamp**, and nothing expires one:

* `ReconciliationAction.Wait` waits indefinitely - there is no deadline
  after which a stuck transfer is failed.
* A record whose node re-registered under a new epoch is skipped on every
  pass, deliberately and permanently (DirectGateway.cs:3753-3756). If a
  node is killed mid-transfer, its record can never be reconciled.

Adding Viktor's minute would mean: a `Began` timestamp on the record; a
`HandoverTransactionTimeout` (default ~60 s); and in the reconcile loop,
any record past its deadline whose nodes cannot both be probed is Aborted
rather than skipped - discard on the target if reachable, thaw on the
source if reachable, release the ownership row so recovery can claim it.

## But it is not the current failure - checked, not assumed

The obvious hypothesis was that killed-mid-handover records accumulate and
strand their partitions. That is measurably false on w19: the live
registry holds **zero** handover records of any state after ~4 h and
thousands of transfers. Nothing is stuck in the transaction.

The strandings observed tonight come from a different place - the route
pin. A partition routed to a connected client cannot be moved, retired or
(before 6c83f49) released; the node drains around it, exports a
HandoverTee or a drain Final, and is then killed with the ownership row
still assigned to it. The row is left owned-but-never-imported with an
export as its newest save. That is the stranded state, and it is reached
without any handover transaction being open at all.

## Recommendation

Build the timeout, but rank it accordingly: it closes a real hole (a node
killed at exactly the wrong moment leaves an unreconcilable record and the
gateway has no way back), and it is cheap. It is NOT the fix for the
stranding seen on w19 - that is the route-pin expiry (a52e1e3, 6c83f49)
plus the rotator's release path. Ordering the two the other way round
would spend the restart on the wrong change.
