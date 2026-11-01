# Plugin data services

The cluster runtime supplies Magnetar's `PluginSdk.Clustering.PluginCluster` provider.
Consumer API, limits and examples are maintained in Magnetar's
[SharedState.md](https://github.com/CometWorks/magnetar/blob/v2.4.2.1/skills/se-dev-plugin-sdk/SharedState.md).
The provider starts after Registry registration, dispatches on the game update thread,
and unregisters during runtime disposal. Managed readiness proves that the cluster
runtime and ordinary plugins share the same SDK provider instance.

Records use the existing Registry lock and store (registry.db, see StateDurability.md), independently of Quasar/Agent.
`POST /registry/plugins/v1` requires node credentials and an admitted physical
node/incarnation. Current record reads and CAS writes are authoritative. Successful
write replay is checked before requiring *new* ownership authority. Lifecycle replay
is checked before closed-node rejection, so a lost stop/restart acknowledgement can
be recovered. Lifecycle outcome, desired-slot change and closure share one store transaction.

Requests travel in a dedicated node-link `Plugin` frame (kind 8); the WA `Global`
channel remains separate. Gateway authenticates the source endpoint/incarnation,
resolves the destination fence and correlates replies. Runtime validates again over
Registry HTTP before enqueueing, then checks incarnation, cached ownership and a
monotonic deadline at game-thread dispatch on the next frame. The deadline is bounded
by the remaining node/owner lease and 250 ms from validation request start, which limits
how long a handler can act on a Registry answer its node's heartbeat has not caught up
with. A deadline missed in the queue, by a long frame, validates again (3 times at most)
and then answers `Fenced`. A message does not grant
exclusive ownership over external side effects: use fenced CAS for authoritative data.

Explicit managed restore changes the plugin store UUID, clears write/lifecycle replay
history and fences old ownership incarnations. Full-downtime binary rollback preserves
current plugin records. `plugins=plugin-records-v1` participates in the storage-format
compatibility gate alongside Registry and world data.

`ClusterRegistry/PluginProtocol.cs` is a verbatim copy of Magnetar's
`PluginSdk/Clustering/PluginProtocol.cs` for the coordinated release, allowing the game-independent Gateway to use
its DTO/store without loading the game-linked SDK. `PluginProtocol.sha256` pins those
bytes. Run `python3 Build/verify-plugin-protocol.py /path/to/magnetar` when refreshing;
release builds check the retained hash. Update both repositories together.

The built release records the compiled SDK's contract version (`pluginSdkContract`) and
its exact hash (`pluginSdkSha256`) in `cli/deployment-capabilities.json`. Quasar accepts a
frozen PluginSdk whose contract has the same major and an equal or higher minor, and
refuses a different binary when either side declares no contract.
This capability requires the coordinated Magnetar 2.4.2.1 release. Public v2.4.2.0 and
cluster v1.0.3 do not supply these services.

Runtime context reports observed WA and owned partition generations, and notifies plugins
when those observations change. PluginStats publishes ownership changes and current
ownership counts/generation without making those observations a substitute for Registry fences.

Authenticated, correlated requests rejected by a changed ownership fence receive `Fenced`;
full dispatch queues return `Capacity`, and a disconnected destination returns `Unavailable`.
Malformed or spoofed frames are discarded. A lost connection/reply can still produce `Timeout`;
callers must not assume an unsuccessful response proves that no side effect occurred.

The World Authority reports Serving only after its global checkpoint/ledger restore has
succeeded and the game session is ready. Fresh worlds validate the supplied checkpoint
instead. Registration alone never grants Serving or managed readiness.

A plugin's `RegisterOfflineBlocker` keeps a partition online (`PluginOfflineService`): the offline sweep and a drain's
offline offer ask each node's blockers about every member, on the game thread, next to the cluster's own reasons. It is
node-local and in memory, so a move or a restart keeps nothing: the blocker of the node holding the grid decides, and a
restarted plugin has to register again. A cluster stop takes every other partition offline. A member with a reason at
the stop gets its partition released instead, so the next start loads it with nobody near (SE-904); the stop waits for
nothing more than for any other partition. The plugin has to register again after that start. Needs PluginSdk contract 1.1.

A plugin's global command (`ExecuteGlobalAsync`) is answered at once, so a World Authority crash can roll it back after
the answer. To act only on what a replacement WA would still know, run the reserved command after it, from a node or the WA:

```csharp
var reply = await client.ExecuteGlobalAsync("cluster.global-save", null, operationId, timeout);
```

`Success` means a recoverable global save (registered, held by a node besides the WA, as for the
[durable commands](StateDurability.md)) holds all the WA did before the command arrived, in any plugin. It needs no
registration and asks for that save; other commands don't wait for it. `Timeout` and the like say nothing: retry with the
same operation id, which keeps its place, because a new id waits for a newer save. A fenced WA and a WA whose saves fail
never answer. `Unsupported` is a cluster from before this command. A cluster with no global saves answers at once.

CLI-launched unmanaged clusters have no executor node plan. Plugin stop/restart requests
therefore return `Unavailable`; operators use the cluster CLI to stop/restart that fleet.

Current records and tombstones are limited to 512 keys per plugin (4096 globally).
Each namespace has a 1 MiB current-plus-replay payload budget; retained replay is
bounded to 32 outcomes/256 KiB per plugin and 128 outcomes/1 MiB globally. The global
current-plus-replay bound remains 8 MiB. Tombstones retain revisions to prevent stale
CAS/ABA; exhausting one namespace blocks its new keys, not another plugin's budget.
Legacy stores retain existing keys even when over a new limit; deletes/shrinks remain
possible, and globally full legacy stores require explicit data migration for new keys.
Gateway write-rate and journal guarantees are in [StateDurability.md](StateDurability.md).

The release-tag SDK documentation link becomes available after Magnetar is released.
For PR review use Magnetar #57 and the exact source pin in
`Build/pr-magnetar-sdk-revision.txt`; review commit pins are not installation instructions.
