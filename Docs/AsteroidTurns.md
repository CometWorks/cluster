# Asteroid turns (SE-1998)

The cluster can turn the world's rotated asteroids to the identity orientation (Forward 0,0,-1, Up 0,1,0) while it runs. Each asteroid turns about its own center, one at a time. Grids, characters, floating objects and bags near the rock turn with it, so they keep their pose relative to the voxels. No save is rewritten.

## How it works

- **The World Authority proposes.** Every `asteroidReset.intervalSeconds` it picks the next rotated voxel map. It goes round robin by storage name and skips rocks with an online player within `asteroidReset.playerMeters`. Then it posts the rock's pivot, axes and storage half size to the Gateway (`POST /registry/asteroid-turns`). Code: `Shared/Cluster/AsteroidTurnService.cs`.
- **The Gateway decides** under the registry lock (`ClusterRegistry/AsteroidTurns.cs`, `Shared/Cluster/AsteroidTurnPolicy.cs`). Distances are measured from the storage box, old pose or new.
  - A static grid in an offline partition within `asteroidReset.staticMeters` refuses the rock as `static:P`. The WA keeps asking, and it keeps being refused while that grid is there.
  - An online partition member within `asteroidReset.gridMeters` refuses it as `online:P`. An offline row a load has claimed counts as online.
  - An incomplete occupancy (the voxel reset's rule) refuses it as `incomplete`.
  - Otherwise the Gateway commits a row. The row pins, for each offline partition with a member within `asteroidReset.memberMeters`, the newest save of every partition in its resolve cover. Any save up to a pin holds pre-turn poses.
- **Every node turns its copy of the rock.** Nodes get the rows from the WA's publish (GlobalStateChannel `asteroid-turns`) and from a poll of `GET /registry/asteroid-turns` every `asteroidReset.pollSeconds`. Each node calls `PositionComp.SetWorldMatrix` on the rock and marks the clients' replicas stale. A rock whose pose isn't the row's old pose is left alone (`error=pose_mismatch`).
- **Offline members turn as they load.** A recovery claim carries the rows whose pins cover its saves. Before any entity is created, `LoadRecovery` first makes sure the node holds every row and has turned its rocks. Then it turns the builders from pinned saves that are near the rock, together with the grids linked to them (rotor, piston, hinge, connector, landing gear). That covers pose, linear velocity and angular velocity.
- **Late members.** In the same frame as the rock turns, a node turns any local member near the old box that no load turned. This only happens in a race and is logged as `late_member`. A frozen member (mid-handover) can't be moved and is logged as `error=frozen_member`.

Rows are never removed. The premade world keeps the old poses, so every node start turns the rocks again from the rows.

## Clients

- No client holds a rock's replicable when it turns, because no player is within `playerMeters` (default 5 km, above sync distance).
- A client that comes near later streams the rock in its new pose.
- An offline base near a turned rock loads already turned.
- Vanilla clients need nothing.
- Impostors (the far view) pick up the turn for most asteroids:
  - An asteroid the Workshop pack covers is drawn from the pack's frame. That frame is already the identity, so it shows wrong before the turn and right after it.
  - A client without the pack gets the new pose from the server's `Moved` resend within about a second.
  - **Gap:** some asteroids have no pack entry, either because they were mined or because the pack never had them. The server streams those, and a client with the Impostors mod keeps the streamed state, old pose included, in its disk cache, keyed by entity id and version. A turn changes neither, so that client goes on drawing the old pose until the asteroid is mined again or the cache entry ages out (30 days). Closing the gap needs a change in Impostors.

## Config

| key | default | |
|---|---|---|
| `asteroidReset.enabled` | false | the WA proposes turns |
| `asteroidReset.dryRun` | true | the Gateway answers `dry-run` with the pins it would take and commits nothing |
| `asteroidReset.intervalSeconds` | 60 | one proposal at most per interval |
| `asteroidReset.gridMeters` | 1000 | an online member this close makes a rock wait |
| `asteroidReset.playerMeters` | 5000 | an online player this close makes a rock wait |
| `asteroidReset.memberMeters` | 50 | offline members this close turn with the rock |
| `asteroidReset.staticMeters` | 50 | a static grid this close keeps the rock as it is |
| `asteroidReset.exclude` | | storage names never to turn |
| `asteroidReset.statusSeconds` | 300 | the WA's `asteroid_turn status` line |
| `asteroidReset.pollSeconds` | 30 | every node's poll of the rows, enabled or not |

## Running it

1. Set `asteroidReset.enabled = true` and leave `dryRun` at true. Watch the WA's `asteroid_turn refused ... reason=dry-run` lines and the status line.
2. Set `dryRun = false`. Each committed turn logs `asteroid_turn committed` on the Gateway and the WA, and `asteroid_turn applied` on every node.
3. To stop, set `enabled = false`. Every committed turn is complete in itself and the nodes converge on it, so nothing is left half done. There is no online undo: restore a backup from before the turn.

Watch for `late_member` (expected 0), `error=frozen_member` (must be 0) and `error=pose_mismatch`.

## Tests

- `dotnet run --project ClusterRegistry -- --self-test asteroid-turns` covers the turn math, the Gateway's commits, refusals and pins, a recovery claim of a pinned save, and that the rows survive a reopen.
- `Test/scenarios_asteroid_turn.py` (`P2-AST-TURN`) runs four rotated asteroids in a real cluster:
  - nothing turns while the feature is off;
  - online grids make rocks wait;
  - offline members are pinned and turned at their wake, keeping their pose relative to the rock;
  - a static grid keeps its rock;
  - a restarted node turns the rocks again.
