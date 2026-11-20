# Lobby anchor administration

A cluster has one lobby anchor. Its durable identity is a station grid; its current partition and serving node can change. Open **Cluster → Partitions → Lobby** to designate an existing station or create one from a Space Engineers grid blueprint. The cluster owns the anchor and its save. Quasar can disconnect after submitting the operation.

## Wire contract

Gateway advertises `lobbyAnchor=v1` through the v1 capabilities response. Quasar exposes the same reads and writes under `/api/v1/clusters/{uniqueName}`. Gateway routes below are relative to `/admin/v1`.

| Method and path | Purpose |
| --- | --- |
| `GET /lobby` | Read the configured station, revision, latest known partition and ownership state. Revision zero denotes an unconfigured lobby. |
| `PUT /lobby` | Bind an existing static grid, or create and bind a new station from an uploaded grid blueprint. |

The write uses manage scope, the existing operation envelope, an `Idempotency-Key` and the observed `expectedRevision`. Reads use query scope. A stale edit requires a refresh. The existing-station request identifies the grid and its observed partition. The create request supplies the XML of a Space Engineers `bp.sbc` file and a remote world position in metres. Show the position before submission. Use a single static station grid for the initial import. Validate the file size, XML, definitions and world placement before activation. An unsuccessful replacement keeps the previous binding. A successful replacement changes which station serves as the lobby and preserves the former station.

The server resolves the current owner. Browsing never creates or moves a station. If the outcome of a create is uncertain, reconcile its durable identity before allowing another placement. UI/API clients display the operation outcome separately from the latest observed availability of the grid. Existing clusters and older Gateway versions retain their normal partition controls.

A configured anchor is the target for first spawn and admin travel. First-visit tracking, respawn gating, the push field and teleporter actions belong to their respective gameplay paths. Applying the anchor configuration does not itself enable those behaviors.

### DTOs and limits

An adoption sends `{"expectedRevision":0,"partition":42,"gridId":12345}`. Creation sends `expectedRevision`, `position` with finite `x`, `y`, `z` coordinates, and `blueprintXml` containing the UTF-8 text of one `bp.sbc` (up to 1 MiB). These two input forms are mutually exclusive. The read response uses the admin envelope with `revision`, nullable `anchor` (gridId, partition, name and position), nullable `owner` and `state`.

With `partition` alone, the node can bind the only static grid in that partition. Supply `gridId` to disambiguate. Query responses include `pendingOperation` during a change. The CLI forms are `lobby` and `lobby-set <JSON file>`.

For an upload, the Gateway checks known live and offline partition bounds. The selected pivot needs more than 101 km of clearance, allowing 1 km for the station and 100 km of separation. The node also checks world bounds, local collisions and installed block definitions. A node never receives a second create dispatch for the same operation. After a lost reply the Gateway searches for the reserved grid identity. If confirmation expires, inspect the grid ID in the operation error before attempting another creation. A replacement changes the binding only after the node reports a catalogued station save.
