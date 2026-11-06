# Node drain and online scaling (T-0312)

How a node leaves a running cluster, and how a new node takes load, without a stop, without content loss and
without disconnecting players. **Which** node starts or stops, and when, is node control's decision (T-0320):
per-host serving/spare targets, `remove`, `recycle`, oldest-first decreases, the drain timeout and `--now` are
described in [NodeControl.md](NodeControl.md). This page is the mechanism underneath.

## Roles

- **Registry**: decides (NodeControl.cs) and drains (NodeScaling.cs `ScaleInPass`). Its state is persisted, so a
  registry restart keeps the targets, the stop requests and the drains in progress.
- **Launcher**: one per host, runs the registry's node plan (`Cluster.actualize_step` in `Cli/cluster.py`): starts
  planned nodes, stops the ones the registry marks Kill, reports every process through the executor heartbeat.

## A new node

A new node (a refill spare, a replacement, a serving node a spare could not cover) is launched with the same
plugin folders and version set as the others: the hub snapshot of a warm node is propagated into its Magnetar
config before it starts, and its dedicated cfg comes from `CLUSTER_NODE_DEDICATED_TEMPLATE` when set. It
registers empty. A spare stays empty; a serving node receives load from the balancer through the normal paced
planner and handover path. The one-transfer-per-pass budget and the per-node single migration still hold, so sim
speed stays within the rotation envelope. A spare activated in place of a recycled node receives that node's
partitions through the drain below.

## The drain

When node control stops a node (remove, recycle, decrease) the node joins the registry's retiring set, and the
automatic loop (never during recovery) drains it:

1. a retiring node takes no recoveries, no offline loads and no close-node drains, and is not replaced by a spare
   if it dies; balancing pauses while a drain is in flight (reason "scale-in drain in flight");
2. every envelope component on a retiring node is pinned to a receiver: for a recycle the spare activated in its
   place, otherwise the node already holding the component's envelope neighbours, else the least projected load.
   The co-location loop then carries each pinned component member by member, clients last, with every
   join/settle/backoff deferral of an automatic handover. Clients switch nodes seamlessly;
3. a retiring node that owns nothing is closed; it reports Empty (the report-driven close also releases rows the
   node no longer reports, T-0312 12f361b);
4. the registry turns the node's plan slot to `Kill`; the launcher stops the process (it usually exits by itself
   first) and reports it `Gone`; the node leaves the live list and goes to the node history.

Entity and network ID ranges are per node epoch and never reused, so a stopped node leaves nothing to reclaim.
Its registry row stays (Empty) because it may still hold catalogued saves, and so does its data dir until
`cluster nodes prune`.

## The forced drain

A stop not finished by its deadline (default 300 s, `CLUSTER_DRAIN_TIMEOUT_SECONDS`; `--now` = at once) is
forced: the registry closes the node with a force-drain deadline of now, and the node's own close drain writes a
Final save of every partition it still owns and offers each one offline (T-0237). A partition routed to a
connected player is released unowned instead and recovers on another node; a client with no partition of its own
on that node is disconnected. That is visible to players, by design. The deadline clock pauses while partitions
recover, since the drain cannot progress then. A forced node is never killed while it still owns rows: it stays
Stopping, with the reason in its stop's detail, until its own drain has released them.

On the bench (T-0320 P5, a 3-partition world with two clients) a graceful drain of a node with a client took
about 5 s end to end and a forced one about 4 s.

## Capacity

A decrease is obeyed. When the measured partition work (sim-µs per second of game thread) spread over the serving
nodes that remain exceeds `CLUSTER_SCALE_IN_MAX_THREAD_SHARE` (0.7) of a game thread per node, the answer carries
a warning with the projected load; nothing is refused.

## Legacy surface (deprecated)

`POST /registry/scale?nodes=N` / `GET /registry/scale` (GatewayAPI.md) and `cli/cluster scale` (ClusterCLI.md)
remain for one release as a shim: `nodes=N` sets the sole host's serving target (409 `multiple_hosts` with more
than one host), `force` and `retire` are ignored. `GET` keeps its fields (`target`, `revision`, `phase`,
`serving`, `starting`, `retiring`, `retired`, `spares`, `blocked` = recovery only, `balance`) and adds `warning`
(capacity) and `hosts[{host, serving, spares, servingActual, sparesActual}]`.

## Rotation

The regression harness rotator recycles, per host, the oldest serving node past its lifetime
(`requestedBy: rotator`): the spare takes over and a new spare refills. It never changes the counts, and it waits
while any node is Starting, Draining or Stopping.

A managed (Quasar) deployment is rotated by the Gateway itself, on `rotation.lifetimeSeconds` (default 2 h): see
"Rotation" in ManagedDeployment.md.
