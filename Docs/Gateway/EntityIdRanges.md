# EntityId allocation: registry-issued sequential ranges

Status: DESIGN ACCEPTED 2026-08-21, replaces the 8-bit node-epoch tag.
Compatibility: NONE. Worlds created under the epoch scheme (w20 and earlier) are dropped;
a fresh world is started with this scheme.

## Why

The epoch scheme tagged bits 48-55 of the 56-bit unique number with a per-node-start byte
issued by the registry and never reclaimed. Rotation spends one byte per node start
(~15/h across 5 regulars + WA), so any world became permanently unstartable after 255
starts, ~17 h - below the 24 h uptime bar. Random low bits also left a birthday-collision
floor that only the 48-bit width kept negligible.

## Layout

`MyEntityIdentifier` keeps its vanilla layout: bits 56-63 = `ID_OBJECT_TYPE`, bits 0-55 =
unique number. The whole 56-bit unique number is now one sequential space:

    unique = rangeIndex << 24 | offset        rangeIndex: 32 bits (1 .. 2^32-1)
                                              offset:     24 bits (0 .. 2^24-1)

Split chosen 2026-08-21 (Viktor): a node that runs out of OFFSETS simply asks for another
range, but a world that runs out of RANGES cannot continue without a full re-index, which we
do not want to implement. So the range index gets the wide field. The first w21 deployment
used 24/32; its 23 issued ranges (index 1..23, each 2^32 wide) were folded into the new
numbering as index k*256 and the next-free counter moved past them (see Migration).

- Range 0 is never issued, so the 56 unique bits are never all zero (EntityId 0 is the
  game's "unset" marker and `ConstructId(type, 0)` must never be produced).
- 4,294,967,295 ranges of 16,777,216 ids. One range per node start plus refills on
  exhaustion (a refill is a single registry POST, prefetched at 90 %; a 20-minute node
  epoch that mints 16 M ids would refill ~once); at 15 starts/h a world lasts ~32,000 years
  of continuous rotation.
- Uniqueness is by construction: the registry issues each range exactly once, a node
  hands out each offset exactly once. No collision check anywhere, including when
  restoring old partition saves - a saved entity id was issued once and, unless the
  same save is imported twice into the world (which handover/recovery must never do),
  it cannot meet itself.
- Ids from `ConstructIdFromString` (hash) are outside the allocator and unchanged.

## Registry (durable authority)

The gateway registry is the durable, atomic authority for the next range, alongside the
existing `NetworkIdBlocks` list (`GatewayRegistry.AllocateNetworkIdBlock`). The World
Authority node is a rotating process and cannot hold the counter durably; it consumes
ranges like any other node.

- State: `EntityIdRanges: List<EntityIdRange(uint Index, string Node, long Epoch,
  DateTimeOffset Allocated)>`; next index = `Count + 1`. Persisted through the WAL like
  every other mutation; a cold start resumes from the persisted list, a rebuild from disk
  carries the persisted high-water mark forward (ranges are never re-issued, even to the
  node that held them).
- `POST /registry/entity-id-ranges?node=&epoch=` -> `{Index, Start, End, Node, Epoch,
  Allocated}` with `Start = Index << 24`, `End = Start + 2^24 - 1`. Requires an Active
  node (same guards as network-id blocks). `GET /registry/entity-id-ranges` lists them.
- Exhaustion (the counter wrapping to 0 after index 2^32-1) is a `RegistryConflictException`;
  it is not reachable in practice.

## Migration from the 24/32 split (done 2026-08-21 on w21)

Old range k covered unique `[k<<32, (k+1)<<32)` = new indices `k*256 .. k*256+255`. With the
cluster stopped, `registry.wal` was rewritten: each old `EntityIdRange` record's `Index`
became `k*256` (the first new-size segment of the old range; the other 255 are simply never
issued) and `NextEntityIdRange` was set to `(maxOldIndex+1)*256`. Ids already minted under
the old split are ordinary 56-bit numbers and stay valid; the only cost is the skipped
segments.
- `NextEntityIdEpoch`, `NodeRegistration.EntityIdEpoch`, the registration response field
  and the rebuild carry-over are removed.

## Node

`EntityIdAllocator` (ClusterPlugin, linked into VRage.Game like the old patch):

- Holds the current range as an immutable `(Next, End)` object with an `Interlocked.Add`
  counter. `NextId()` and `NextIds(count)` are lock-free on the fast path: reserve
  `count` offsets with one `Interlocked.Add`, return the first. Crossing `End` takes a
  lock, installs the prefetched range (fetched at 90 %) or fetches one synchronously (one
  POST per 16 M ids), swaps the range object and retries; offsets reserved past `End` are
  simply abandoned (gaps are free).
- Prefetch at 90 % of the range so the swap never waits on HTTP in the game thread.
- `MyEntityIdentifier.AllocateId` patch: when the allocator is configured the unique
  number is `EntityIdAllocator.NextId()` for BOTH allocation methods (random and
  sequential); type byte untouched; `MarkIdUsed`/`m_lastGeneratedIds` stay vanilla and
  become irrelevant. Unconfigured (standalone server, codec tests) -> vanilla behaviour.
- `MyEntityIdentifier.AllocateIds(count, out first)` is the batch entry point; it returns
  `true` with a contiguous run in cluster mode and `false` otherwise.
- Tight loops allocate in batches of 100 through `MyEntityIdBatch` (a small struct:
  `Next()` draws from a local run, refills with `AllocateIds(100)`, falls back to
  per-id `AllocateId` outside cluster mode). First user: `MyEntityIdRemapHelper`
  (blueprint/projector/paste remapping). This removes ~99 % of the allocator traffic
  from those loops; unused tail ids are abandoned.
- Partition ids: the registry allocator (`/registry/partition-ids`) stays the primary;
  the local fallback `PartitionManager.NewId` uses the node's current entity-id range
  index in the top 24 bits instead of the epoch byte.
- `CLUSTER_NODE_EPOCH` env override is removed.

## Verification

- Gateway `SelfTest`: consecutive ranges are distinct and disjoint; restart resumes
  past the persisted high-water mark; rebuild never re-issues; range 0 is never issued;
  exhaustion (counter wrap) is refused.
- `ClusterPlugin.CodecTest`: allocator returns sequential ids, type byte preserved,
  unique never 0, `NextIds(100)` is contiguous, crossing the range end switches ranges
  and abandons the reserved tail, concurrent `NextId` from 8 threads yields no
  duplicates; `MyEntityIdBatch` fallback path.
- Live: fresh world, regression cluster, rotation past 255 node starts (~17 h) without a
  registration refusal; grep node logs for `entity_id_range allocated`.

## Follow-up ceiling

NetworkId blocks are 2^32 / 2^16 = 65,535 grants (T-0264), never reclaimed while the cluster runs; a registry start
that finds no node Active or Closed frees all of them (T-0344).
