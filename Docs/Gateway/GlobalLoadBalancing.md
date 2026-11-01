# Global load balancing (Viktor's 2026-08-24 design)

Replaces the pairwise heaviest-vs-lightest `ChooseBalanceMove` and every hard limit it
carried (`BalanceMaximumMovePcu`, `BalanceMinimumPcuGap`, `BalanceBandPercent`,
`BalanceImprovementMarginPercent`, `BalanceFreezeBudgetMs`, the majority-mover guard,
the ThreadLoad agreement gate). Design ruling, verbatim in substance:

> The cluster should pack PCU into the nodes, each around the average PCU of the
> cluster. Minimize moving (especially large) partitions - moves cause player-visible
> lag plus CPU/memory cost. No hard-coded limits on anything: they cannot be tuned in
> the generic case. The cluster always converges to a "good enough" assignment;
> partitions move only when the gain in PCU balance is expected to lower the risk of
> simspeed drops on the most loaded node relative to the average of the others. The
> ONLY tunable is the acceptable risk level at which partitions stop moving.

## Mechanism

1. **Global view, not a pair.** Every pass with no co-location work pending, compute
   each serving node's PCU load. Let `H` be the most loaded node and `m` the mean PCU
   of the others. The relative excess `e = (L_H - m) / max(m, 1)` is the risk proxy for
   "simspeed-drop risk on the most loaded node vs the average of the others".
2. **The single tunable**: `BalanceRiskTolerance` (cost-model JSON, ops-overridable).
   While `e <= tolerance` nothing moves - converged, good enough. There is no other
   gate: no size caps, no absolute PCU floors, no freeze ceilings.
3. **Unit of movement is the envelope component** (co-location invariant: partitions
   inside one another's envelope must share a node). Candidates: every component on
   `H`, to every other serving node - any size. #1304: the planner groups at the
   co-location ENGAGE distance (`ColocationEngageFraction` x envelope, 3500 m deployed),
   not the envelope, so a lane chain linked at 3.5-4 km is several units. A unit moved
   away leaves only cross-node pairs co-location does not engage on (it engages below
   the same distance), so the move is not dragged back. At fraction 1.0 the units are
   the envelope components.
4. **Selection minimizes peak PCU per unit of lag**: a candidate must strictly lower
   the cluster's peak PCU; among those, the one with the highest
   `(peak reduction) / (estimated migration lag)` wins. Migration lag is the §7.5 cost
   model's freeze estimate (bytes + measured per-entity commit stall) plus the client
   switch term, summed over the component's members. Large partitions are therefore
   moved LAST and only when nothing cheaper achieves the balance - emergent from the
   pricing, not from a cap. When only a giant reduces the peak and `e` is still above
   tolerance, the giant moves: all partitions remain moveable.
5. **Execution is a pinned migration, not a one-shot transfer.** Initiating a
   rebalance pins every member of the chosen component to the target node
   (`_balanceMigrations`). The co-location pass carries the units holding a pinned
   member, not the rest of their envelope component (#1304). The co-location pass consults the pins when deciding a
   component's home, so it CARRIES the migration member-by-member (reason
   `balance-migration`) instead of dragging the first mover back - this retires the
   old majority-mover guard, which existed only because a minority-first move could
   not flip the pairwise home. Real envelope violations still preempt migration moves
   (migration urgency is floored at the envelope). Pins renew on progress and expire
   (180 s without progress) so a dead migration reverts to normal cost-based homing.
6. **Pacing**: one migration is INITIATED per `BalanceCooldownSeconds`; the member
   moves ride the ordinary one-transfer-per-pass co-location loop (§7.3 sequential
   freezes).

## What this fixes (w23 measurements)

- Two ~79k space-area components stuck on a 595k node behind the majority guard
  (findings/node2-imbalance-two-blockers-not-the-margin.md) - now migratable.
- The 1204-PCU pebble churn (73% retraction) stays dead: a pebble cannot strictly
  lower the peak by a competitive amount, and below tolerance nothing moves at all.
- The consolidated 292k/324-entity blob partition is no longer categorically frozen
  by a 5 s budget: it moves when it is the only way to reduce the peak, priced last.

## Calibration

`BalanceRiskTolerance` default 0.25 (the hottest node may exceed the mean of the
others by 25% before movement is warranted). The cost model's freeze coefficients
(`FrozenMsPerByte`, `FreezeMsPerEntity`, `StallFloorEntities`, `GammaPerSwitch`)
price the lag side; they are measured calibration, not limits - nothing is refused by
them, only ordered.

## The approximate placement optimizer (2026-08-24, second round)

Viktor: "We need some approximate optimizer for this, at least during startup. It can
be stochastic, does not have to be exact." `OptimizePlacement` treats the cluster as
bin packing: envelope components are indivisible bag items with a home bag and a
priced inter-bag move; no stacking order. Multi-start jittered-LPT plus relocate/swap
local search, fixed seed (same inputs, same plan). Objective, lexicographic: an
assignment inside the tolerance is *good enough* and the cheapest one wins; otherwise
lowest peak, then cheapest, then most spread (the emptier-target rule generalized).

Two modes over `ChooseRebalancePlans`:

- **Idle cluster (zero clients - startup, the nightly window)**: the whole optimal
  reassignment pins at once and the trains run unpaced. Nobody is playing, so pacing
  protects no one; startup imbalance converges in one pass instead of one component
  per minute. This is "choose a good balance right at startup" implemented as instant
  convergence - initial claim order stays player-priority-driven and warm nodes still
  reload their own worlds (the cheapest load path).
- **Live (clients connected)**: unchanged pacing and strict-peak-lowering, the
  optimizer supplying the target.

**Achievable-optimum gate** - the second use of the same single tunable: the balancer
idles when the current excess is within `BalanceRiskTolerance` of the OPTIMAL
assignment's excess. A world whose mass is one atomic component keeps its excess far
above tolerance forever; without this gate the balancer sheds 1204-PCU pebbles once a
minute at the floor (w23 17:01) for gains nobody can feel.

Balance initiation also waits for recovery to drain (`GatewayRegistry.RecoveryPending`)
so cold-start plans are made against the cluster's real shape, and migration pins renew
on ANY member progress so multi-target full plans cannot starve a train's pins.

The remaining floor is partition membership itself: see
cluster-regression/findings/blob-atomicity-is-a-bounding-sphere-artifact.md - the w23
"atomic" 422k blob is three >=4km-separated content clusters glued by inflated
bounding spheres; a server-side membership split is the lever the optimizer cannot
reach.

## Heavy components ([DF-683](https://tasks.cometworks.se/tasks/1454), 2026-10-23)

Viktor's rule: the balancer must not move a partition with many online players. The move
stalls every one of them and relieves nothing, because the cost travels with the
partition. Observed in [DF-488](https://tasks.cometworks.se/tasks/1191) run 2 (octahedron-1191e, hub phase 2026-10-22): the Alpha hub
was one anchored partition with 58-74 clients and ~400k sim-us/s. Its node ran at sim
~0.97, so `EffectiveNodeLoad` read ~1.03M for that node, with ~600k of it unattributed
overhead fixed to the node. The planner moved the hub off the "slow node" (kind client,
estimated lag 36-38 s), the next node became the slow one, and the hub was moved again on
every pass the residence allowed (17 moves in run 2).

**Heavy.** A balance unit (the planner's units, `Components(units: true)`) is heavy while
it carries at least `HeavyClients` clients (deployed 30). It stays heavy until its clients
drop below `HeavyReleaseClients` (deployed 20), so a crowd that comes and goes does not
flip the rule on every pass. `0` turns the feature off, and every decision is then
bit-identical to the planner without it. The gateway latches the set once per pass
(`DirectGateway.UpdateHeavy`), logs `Heavy component pinned on ...` and
`Heavy component released on ...`, and publishes it in `GET /balance/status` (`heavy`,
`heavyClients`, `heavyReleaseClients`).

**What the balancer does around a heavy unit** (`PlanHeavyMoves`, ahead of every gate, one
move per node per pass, node-disjoint like every paced plan):

| Case | Rule |
|---|---|
| 1. one heavy unit | It is never moved by the balancer. Its node takes no balance plan, and the optimizer treats the node as closed. |
| 4. neighbours on its node | They are evacuated, biggest load first, to the least loaded node without a heavy unit. This passes no gate (tolerance, load floor, peak-drop), so the neighbours leave before the node saturates. Residence does not hold them. |
| 2. two heavy units on one node | The one with fewer clients that is not cooling down moves once (`heavy-split`), to an empty node if there is one, else to the least loaded node without a heavy unit. After that both are pinned. |
| 8. empty node | When the neighbours carry more clients than the heavy unit and an empty node is free, the heavy unit moves there instead (`heavy-dedicate`): fewer clients pay a migration. Otherwise the empty node is the first evacuation target. Hot spares stay outside placement until activated (HotSpareNodes.md). An activated spare, or a recycled node's replacement, is such an empty node. |
| 7. ping-pong | A heavy unit moved by a split, a dedicate or a drain is not moved again by the balancer for `HeavyCooldownSecondsPerClient` (60) x its clients seconds. No plan targets a heavy node, so an evacuated unit cannot come back. |
| 3. shrinks | Below `HeavyReleaseClients` it is released and balanced like any other unit. |
| 5. merges | In co-location the heavy side is home and the other side comes to it. When a merge creates a heavy unit (both sides, or neither alone), the side with more clients stays (equal: the lighter node), and the light units on that node are then evacuated. That is the cheap side of the cost model. A teleport keeps its own rule, and a HandoverBlocked side still wins. |
| 6. drains | Close-node, the drain catch-up and scale-in/recycle move a heavy unit once, to the recycled node's replacement or else the emptiest node without a heavy unit. Light units keep off that node unless an envelope neighbour is there. |

A free offline load (no node named) avoids heavy nodes while another node can take it. A
hub node holds few partitions, so the fewest-partitions rule used to pick it.

The kinds show in the pin log as `kind evacuate`, `kind heavy-split` and
`kind heavy-dedicate`. Scenario tests: `--self-test balance-heavy-*` (control, pin, split,
release, early, merge, drain, spare). `balance-heavy-pin` replays the DF-488 hub shape: over 60
passes the control moves the hub 54 times (3240 client migrations), while the heavy rule
moves it 0 times and evacuates its 3 neighbours (1 client migration).

This does not split the hub. A hub too big for one node's game thread stays at that node's
knee. [DF-669](https://tasks.cometworks.se/tasks/1436) is the complementary fix, splitting an indivisible hub partition.
