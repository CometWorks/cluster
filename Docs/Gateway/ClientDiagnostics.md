# Client diagnostics and rotation publication

The Cluster Registry owns the diagnostic snapshot. The Cluster Manager owns rotation
execution and publishes assigned due times. Quasar reads and forwards this same contract.
Players receive diagnostics through the node's secure mod-message channel, without
HTTP credentials or local lifetime files.

## Install and open F11

The release includes `client/ClusterDiagnostics/plugin.dll` and `plugin.xml`. Copy that
folder to Pulsar's `Interim/Local` folder and enable it. Source/dev registration uses
`ClusterDiagnosticsClient.xml`. The packaged DLL targets .NET 10. The server responder
is part of both cluster role plugins; no separate diagnostics companion is needed.

Open **F11 → Cluster diagnostics** or the plugin's settings button. When vanilla blocks
its multiplayer mod menu, F11 opens only the read-only diagnostics screen. Escape, F11
and the close button close it; vanilla debug tools remain disabled.

Player mode shows the current game node, endpoint, registration age, process uptime,
serving age, rotation due state and drain deadline. Admin and Owner additionally receive
the bounded roster, World Authority generation, node epochs/leases and Registry status.
Moderator and Space Master remain in player mode. Rank is checked on the game thread
before each unicast; player packets physically omit other-node and Registry fields.

Mod-network channel **48739**, protocol **4**, carries reliable unicast responses to
session-nonce requests. Requests, queues, strings, packets and roster size are bounded.
Notifications report observed switches and incarnation changes, with up to three seconds'
normal polling delay. Disconnects clear the cache; demotion clears advanced data.

The node reads `/registry/client-diagnostics?node=ID&epoch=EPOCH` using its existing
join credential. Incarnation validation and capture share the Registry lock. Only
allowlisted diagnostic fields leave this endpoint, never credentials, control URLs,
paths or raw state. Registry failure clears the cached fleet and rotation schedule.

## Manager and Quasar publication

Admin contract `0.10.0` adds capabilities `rotationPublication: v1` and
`clientDiagnostics: v1` with these wire-v1 operations:

- `GET /admin/v1/rotation`: current revision, source, validity and applicable assignments.
- `PUT /admin/v1/rotation`: replace the fleet publication, using manage scope,
  `Idempotency-Key` and the existing durable operation/replay path.
- `GET /admin/v1/client-diagnostics?node=ID`: query-scope diagnostic snapshot.

`RotationPublication` contains `expectedRevision`, `source`, `validUntil`, `enabled`
and `nodes`, where each assignment is `{node, epoch, dueAt}`. Validity is at most five
minutes. Disabled rotation has an empty list. Missing assignments mean unknown; no
fleet estimate is substituted for an actual stagger/jitter assignment.

Bridge the Manager's existing lifetime publication on the Manager host:

```sh
cli/cluster --root /path/to/run nodes publish-rotation \
  --lifetimes-file /path/to/manager/node-lifetimes.json --valid-for 120
```

Call this from the existing Manager loop after assigning lifetimes and renew every
30 seconds. It checks `rotatorPid` locally, reads `nodes.<id>.lifetime`, derives due time
from `Activated` (falling back to `Registered`), and binds each current Registry epoch.
A bad file or dead writer fails rather than renewing old data. It does not execute a
rotation. Other Managers can use `cli/gateway-admin rotation-set publication.json`;
`rotation` reads the revision and `client-diagnostics --node ID` reads the report.

Quasar exposes GET/PUT `/api/v1/clusters/{cluster}/rotation`, the matching
`/client-diagnostics?node=ID` read and command action `rotation-set`. The publication
is a full fleet replacement, not a per-host merge. Multiple writers must coordinate
through the expected revision. Source names are diagnostic labels, not credentials.

Publication and revision persist across restart. Assignments stay hidden until live
serving observations return and disappear from reads on expiry, epoch change, spare
designation or drain. Clients enforce the publication lease between packets using
server time plus elapsed time since receipt. **Rotation due in / overdue by** is an
eligibility time, not a promise of immediate execution. Drain deadlines stay separate.

## Verification and rollout

```sh
dotnet run --project ClusterRegistry -- --self-test client-diagnostics
dotnet run --project ClusterRegistry.Cli -- --self-test
dotnet run --project Test/ClientDiagnostics -- /path/to/SpaceEngineers/Bin64
(cd Cli && python3 -m unittest test_client_diagnostics)
```

Game stubs test the production feed, codec and client. Installed metadata verifies F11
targets; live layout, Direct Transport handover and rank changes still need a game session.
Deploy matching cluster and client versions together. Old Registries report the new
capabilities as unavailable; unrelated Quasar pages remain usable.
