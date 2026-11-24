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

The table in `Shared/Cluster/ModApiPolicyTable.cs` covers public members in the ModAPI namespaces and
`MyVisualScriptLogicProvider`, plus concrete game members that Workshop mods call. It keys methods by
parameter signature. The two `IMyPlayerCollection.TryGetIdentityId` overloads illustrate why. Passing a Steam
ID returns the World Authority’s identity binding. Passing an identity ID asks for a live player object and
can return null when the player is on another node. A VSL GPS operation for all players similarly reaches
only the node’s own roster.

Each row records a policy, breakage class, possible shim, mod-channel operation, Workshop mod count, and
reason. For a concrete member that implements a ModAPI interface, the reason names the interface contract.
A declaration reference locates the API in DS 1.211.018. Rows marked `UNCERTAIN` need a cluster check.
A `?` for breakage and shim means both are unknown. A downstream verdict must preserve that uncertainty. Mutable definition and settings fields have a
per-node write policy. Reading the stock value (for example, a block definition’s PCU) is consistent across
nodes that load the same content. The scanner counts field stores and property setters toward B7 for these policies. Reading a scalar still contributes to member frequency. Taking a field address or loading a mutable reference can allow a later write through an alias, so those sites remain unverified. For VSL callback fields the scanner counts assignments, including delegate subscription and removal. A bare delegate read alone leaves its use unresolved.

`Test/ModApiPolicy/CorpusMembers.txt` pins the distinct resolved member keys from the subscription-ranked
Workshop corpus collected on 2026-11-22. It includes the ranked items and dependencies. There were 482 scripted
mods and 534 script groups. In 78 groups the dedicated-server compiler reported errors. The scan includes
members Roslyn could bind in those groups. Dynamic and unresolved calls have no entry in the counts.

The release build checks the table against the dedicated server and the pinned keys. To check it locally,
run the project from the repository root. To also check every resolved call in a Workshop content directory,
pass the path to the game's numbered mod directories as the last argument. The scanner reads unpacked
scripts and legacy archives. `--scan` writes the current member keys and distinct mod counts as TSV to stdout.

```bash
dotnet run --project Test/ModApiPolicy -c Release
dotnet run --project Test/ModApiPolicy -c Release -p:Dedicated64="$DEDICATED64" -- "$DEDICATED64" --check-corpus /mnt/hdd/se1-mod-corpus/244850
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

## Offline Workshop corpus report

The [verdict table](ModCorpus/verdicts.tsv) covers 1,500 subscription-ranked mods and 18 loadable dependencies from the November 2026 catalog. One additional Workshop dependency is a programmable-block script. The scan used the November 24 script inventory and Dedicated Server 1.211.018 on Linux. The catalog SHA-256 is `a864b1b720a50da3aec54f0e5b67a1617dcb40c0cadcb2d0907e5021e2a7f763`.

| Static verdict | Ranked | Extra dependencies | Interpretation |
|---|---:|---:|---|
| A | 1,027 | 9 | No compiled scripts, confirmed against the script inventory. Content can still fail to load. |
| B | 8 | 1 | The scanner found no flagged member or state pattern. |
| D | 255 | 5 | A member or state pattern calls for adaptation or a functional check. |
| U | 210 | 3 | Compilation or policy evidence is incomplete. |

B-state denotes isolated mutable entity logic, C denotes session state suitable for WA-only placement, and S denotes risks covered by named shim candidates. This run assigned none of these three verdicts. X requires a human decision about whether adaptation is feasible. The `runtime_class` column is the existing scanner's A–D placement input. Use it to compare a live load record with a successful offline compile.

The [member table](ModCorpus/members.tsv) counts IL references to methods and fields, including property and event accessors. Each mod contributes its lifetime subscription count once per member it uses. Multiple subscriptions from the same player can occur in different mods. The [summary](ModCorpus/summary.tsv) applies the same weighting to breakages and shim candidates. A mod may appear under several candidates. A candidate count measures exposure to a change, even when the mod also needs another fix.

B2 includes static or session fields holding collections of entities. B4 counts mutable instance fields declared on entity components. B6 includes direct world-changing calls in session simulation callbacks. These are candidates for behavioral testing. A scratch collection reused during a scan, for example, can produce a B2 hit even if it retains no entity between ticks. Indirect calls through mod helpers, subscriptions made conditionally, entity IDs stored as integers and mutable objects behind readonly fields need further inspection. Role-check sites count reads of `IsDedicated` and `IsServer`. A read alone does not establish which branch guards a later action.

Fifty-four Workshop items had offline compile diagnostics. U also covers uncertain or unmapped IL members when the scanner has no stronger reason for a D verdict. Compiled groups from a partially compiling mod still contribute observed sites to the frequency tables. The compiler uses the installed DS compatibility substitutions and protobuf tag pass. It can retry with a newer C# language version and does not run the game's whitelist or the optional DotNetCompat plugin. Compile diagnostics and live cluster errors are recorded separately.

Build and regenerate from the cluster repository root, with the directory containing the DS assemblies in `SPACE_ENGINEERS_BIN64`. The corpus directory contains `catalogue.tsv`, `scripts.tsv` and the Workshop folders under `244850/`.

```bash
dotnet build Tools/ModCompatScan -c Release -p:Dedicated64="$SPACE_ENGINEERS_BIN64"
scanner=Tools/ModCompatScan/bin/Release/net10.0/ModCompatScan.dll
python3 Tools/ModCompatScan/check_evidence.py "$scanner"
python3 Tools/ModCompatScan/corpus.py "$scanner" /mnt/hdd/se1-mod-corpus Docs/ModCorpus
```
