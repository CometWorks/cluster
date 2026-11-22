# Mod compatibility: classes, placement and the load record

T-0242 (Vikunja #260). The world's mod list is global: every node and the World Authority (WA) load every
mod. What differs per mod is whether its code still behaves on a cluster, and where its session components
should run. `Shared/Cluster/ModCompatScanner.cs` decides a default; `Shared/Cluster/ModCompatService.cs`
applies overrides, enforces the placement and logs the result.

## Classes

| Class | Meaning | Default placement |
|---|---|---|
| A | no compiled scripts (blocks, definitions, textures) | everywhere |
| B | entity-local scripts: game-logic components, no world scans, no client messaging | everywhere |
| C | global logic in session components only, keeping world state (SetVariable, world storage writes, a session object builder) | wa-only |
| D | needs the cluster: world scans or physics queries (grid reach), or client messaging (multiplayer sends, VSL notifications); correct only when adapted to the mod channel | everywhere, or adapted when the mod uses the channel |

The scan reads the IL of every compiled mod assembly (one per `Data/Scripts` subfolder) and counts call
sites per API group: `session-components`, `game-logic-components`, `session-state-persisted`,
`entity-world-scans`, `physics-queries`, `client-messaging`, `set-variable`, `file-storage-reads`,
`file-storage-writes`, `damage-system`, `vsl-notifications`, `mod-channel-handshake`. The rules, first match
wins: no scripts is A; the mod channel ids is D adapted; grid reach or client messaging is D; game-logic
components is B; session components with world state is C; anything else is B. A mod with game-logic
components is never C by default, because they may expect the session component that WA-only placement skips.

Local and global storage are per machine, which here means per node, and mostly hold logs and client settings.
They are reported and counted but do not make a mod C.

## Member policy table

`Shared/Cluster/ModApiPolicyTable.cs` (SE-1840) goes one level below the groups: one row per member of the mod API
(`Sandbox.ModAPI`, `VRage.ModAPI`, `VRage.Game.ModAPI`, `SpaceEngineers.Game.ModAPI` and their sub-namespaces,
plus `MyVisualScriptLogicProvider`), with its cluster policy (`local`, `replicated`, `node-scoped`, `per-node`,
`affinity`, `client`), the breakage class B1-B8 of the clustering plan's `Modding.md`, whether a cluster-side
shim could fix it, the mod channel operation that replaces it, how many local Workshop mods use it, and a
file:line reason from the decompiled game. The file's header explains the columns. The scanner does not read
it yet. `Test/ModApiPolicy` fails when the game has a member without a row or a row names a member the game
dropped; it runs in the release build against the DS it downloads, or by hand:

```bash
dotnet run --project Test/ModApiPolicy/ModApiPolicyChecks.csproj -c Release -- <DedicatedServer64 folder>
```

## Overrides

Precedence: admin > author > automatic. The scan always runs, so a mod update that changes its API use shows
in the record even when an override decides.

- **Author**: a file `cluster-mod-compat.txt` in the mod folder (first line that is not a `#` comment), or a
  string constant `ClusterModCompat` on any type of the mod (`public const string ClusterModCompat = "D adapted";`).
  The file wins over the constant.
- **Admin**: `cluster-mod-placement.txt` in the process's mods folder (`<data>/Mods`, staged with the mods), or
  the file named by `CLUSTER_MOD_PLACEMENT`. One `<mod id> <verdict>` per line, `#` comments; the id may carry
  the `.sbm` suffix.

A verdict is `<A|B|C|D> [placement]` or a placement alone. Placements: `everywhere` (synonym `nodes`),
`wa-only` (synonym `wa`), `adapted`. A class alone implies its default placement (C wa-only, D adapted, else
everywhere); a placement alone keeps the automatic class.

## Enforcement

Only the placement is enforced. On a regular node of a cluster, a `wa-only` mod's session components are not
registered (`MySession.RegisterComponentsFromAssembly`); the WA and a standalone server register them. Its
game-logic components still run wherever their entity is. No mod is ever refused.

## The load record

Every process writes, for every mod at every session load:

- `[ClusterPlugin] cluster_mod_compat mod=<id> name=... role=<WA|Regular|Standalone> class=... placement=...
  here=... decidedBy=<automatic|author|admin> auto=... author=... admin=... il=... groups=... storage=...`
- `[ClusterPlugin] cluster_mod_compat_json {...}`, the same as JSON: `mod` (id, name, service, folder, path),
  `assembly` (names, `ilSha256`: SHA-256 over the IL, stable across compiles of the same sources), `class`,
  `placement`, `here` (`everywhere`, `adapted`, `wa-only:this-node-is-wa`, `wa-only:skipped-here`,
  `wa-only:standalone-loaded`), `skipped`, `decidedBy`, `automatic` (class, placement, reason, every group with
  its count and first call site), `author` and `admin` (value, source, parsed class and placement, or `"NONE"`),
  `storage` (per tier used: this node's absolute path and read/write, or `"NONE"`), `warnings`.
- `[ClusterPlugin] WARN cluster_mod_compat mod=<id> ...` per disagreement between the verdicts, per ignored
  override, and for a wa-only mod with game-logic components.
- `[ClusterPlugin] cluster_mod_compat summary role=... mods=N A=.. B=.. ... skippedHere=N` after the last mod.

Diffing the JSON records of two starts is the regression check for a mod update.

## File writes

Writes and deletes through the mod file API (`Write*FileIn*Storage`, `DeleteFileIn*Storage`) are counted per
mod and tier. The first write of each pair is logged; on a regular node it is a WARN
(`cluster_mod_storage_write`), because it lands in that node's own storage and only the WA's copy is kept
cluster-wide. `GET /cluster/mod-compat` serves the records and the counters (`storageWrites`).

## Offline report

`Tools/ModCompatScan` compiles mod folders the way the game's script manager does and runs the same scanner:

```bash
cd Tools/ModCompatScan && dotnet build -c Release -p:RunPostBuildEvent=Never
dotnet bin/Release/net10.0/ModCompatScan.dll [--json] [--expect expected-classes.txt] <mod folder>...
dotnet bin/Release/net10.0/ModCompatScan.dll --selftest
```

`TestMods/` holds one fixture per rule; `expected-classes.txt` pins them and the T-0242 test corpus.

## Not built, on purpose

A transparent grid/entity proxy for WA-hosted mods, a shared or proxied file system for node-hosted mods, and a
second "dedicated node" role (#260 section 2). The mod channel extensions a class D mod needs (role, owner routing,
player nodes, handover hooks, channel reset) came with #1280: Docs/ModChannel.md, and the reference adaptation
`TestMods/fixture-d-reference`.
