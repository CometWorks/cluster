# Co-location ping-pong: the pairwise pass disagrees with itself about the component

Date: 2026-08-24, w23. Measured across four gateway processes on the live regression cluster.

## What happens

A partition is handed back and forth between two nodes about once a second, every transfer a real
handover (begun/captured/imported/committed/released, ~200-300 ms of game thread on BOTH nodes), for
as long as the geometry lasts. It is not rare and it is not one sick partition:

| gateway process | co-location transfers | reversals | worst single partition |
|---|---|---|---|
| 12:35 run, 108 min | 1068 | 714 (67%) | 9681, 494 reversals in 21 min |
| 14:29 run, 25 min (cooldown present) | 113 | 0 | - |
| 15:06 run, first 20 min | 368 | 270 (73%) | 5495, 271 transfers in 7.5 min |

## The mechanism, and it is NOT gap noise

Partition 5495, 15:09:59 to 15:17:28, alternating every second between node-2 and node-3, with the
SAME two numbers on every cycle:

    node-2 -> node-3   envelope gap  -3216.7m   component home target of 18 ... 25
    node-3 -> node-2   envelope gap +10356.3m   component home source of 24 ... 25

Identical gaps each way means the geometry is static: nothing is drifting, no threshold is being
crossed and re-crossed. What changes between passes is the COMPONENT ROSTER - it grows from 18 to 25
members while this runs - and with it which side `HomeIsTarget` calls home.

`ChooseMove` solves the envelope-connected component PAIRWISE, per node pair (DirectGateway.cs, the
`for i / for j` loop over reports). A component that spans three or more nodes is therefore solved
several times per pass with a different member roster each time, and the passes disagree. Each one
correctly moves the partition to the home ITS roster implies; the next one moves it back. The comment
above that loop already says the pairwise solve is approximate with more than two nodes - this is what
that approximation costs.

The second, smaller mechanism from the same code: `HomeIsTarget` compares two costs summed from
`EstimatedSnapshotBytes`, which jitters ~0.4% between reports (13277 / 13324 / 13278 on consecutive
transfers of one partition), with an exact `!=`. A component whose sides are near-equal flips home on
that noise alone.

## What this means for the fix

A margin on the cost comparison alone would address the second mechanism and NOT the first. 5495's
sides were not near-equal - the flip came from each pair seeing a different roster. The rework needs
the home decided ONCE per component across ALL the nodes it spans, with a margin as the tie-break for
the genuinely near-equal case. Design agreed with the Node balancing session, 2026-08-24 15:35.

Blocking the resulting move is the wrong instrument, whatever it measures: co-location is a
correctness move, and a partition whose grids really did move needs to go back, inside 30 s if that is
what the geometry says (Viktor's ruling, 2026-08-24). The 30 s no-return cooldown (850aa75) was
deployed 14:29:13, measured, and reverted at 15:06:56 (4601db7).

## What the cooldown's window did show

Storms in both cooldown-free runs at the same node age, none in the cooldown run:

| 0-10 min bucket | transfers | reversals |
|---|---|---|
| 12:35 run, no cooldown | 119 | 14 |
| 14:29 run, cooldown | 115 | 0 |
| 15:06 run, no cooldown | 363 | 270 |

Keep this as evidence that the problem is LIVE and frequent, not as a case for the cooldown's return.
The caveat I raised against my own result at the time - 95% of the 12:35 run's reversals came from
four partitions that had retired by 14:50, so the storm had a reason to be absent - is weakened by the
15:06 run, where a fresh partition reproduced it within three minutes of the harness starting.

## How to measure it

`cluster-regression/bin/colocation-flap-report [log]`: co-location transfers, reversal share, ALARM
volume and balance moves, bucketed by minutes since the log's first line.

Read buckets at EQUAL minutes-since-restart. A restart synchronises every node age, so the first
rotation round is a catch-up burst: in the 12:35 run 405 of the 427 transfers in the 10-20 minute
bucket were reversals. Comparing a post-restart total against a whole-run total credits a fix with a
burst that simply did not recur.

Check whether a flapping partition still EXISTS before crediting anything with its absence. Three
flappers resolved themselves today (9681, 9837/9903/9889 retired; 5495 stopped on its own at 15:17:28
while still live on node-3, generation 343).
