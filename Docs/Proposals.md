# Proposed changes outside `~/ws/se/se1/cluster` (need Viktor's confirmation)

Nothing here has been applied. Each item names the repo, the reason, and the change.
(The three cluster-regression items found on w25 - Magnetar process matchers, `harness-up` unable to
stage a platform subset, `setup` exiting silently during a rotation - were implemented in
cluster-regression f24dbbc on 2026-09-02 and confirmed on w26; removed from this list.)

## 1. Magnetar / Pulsar: key dev folders by folder + manifest

Repo: `~/pulsar` (Magnetar's `Pulsar/Shared/PluginList.cs`). `devFolderPlugins` is keyed by
`Path.GetFullPath(folder)`, so two manifests in one repository (ClusterNode.xml and
WorldAuthority.xml in `~/ws/se/se1/cluster`) collide and only the last survives. The one-line fix and the
current trailing-slash workaround are in `Docs/Magnetar.md` ("Registration"). Once it lands,
drop the trailing slash in `Cli/magnetar_config.py`.

## 2. Publicizer batch A caveat

`Shared/Tools/GameAssembliesToPublicize.cs` vs `PublicizedEvents.cs`: `MyEntity.OnClosing` and
`MyDataReceiver.OnBroadcasterFound/Lost` need `DoNotPublicize` under msbuild. Inside `~/ws/se/se1/cluster`,
listed here only because it touches the publicizer contract shared with Magnetar's compiler.

## 3. cluster-regression: one headless client launch recipe

Repo: `~/ws/se/se1/cluster-regression`. Its `GameClient` local-launch path and this repository's
`Test/clients.py` implement the same Pulsar headless recipe (own HOME per client, shared GitHub
cache, `Remote.cfg` per port, `ExperimentalMode`, `PULSAR_NO_RENDER`). Once the bench recipe has run
the whole suite, either the harness imports `Test/clients.py` from a package checkout, or the two
stay in sync by review. The harness also calls the node route `POST /cluster/igc-test-antenna`
(`bin/antenna-ab`); the route stays in the product for it, and a rename to a non-test name would
need the tool changed in the same step.

## 4. cluster-regression: one premade world copy per node

Repo: `~/ws/se/se1/cluster-regression`. `regression/boot.py` seeds every node's
`SpaceEngineers-Dedicated.cfg` with the shared split world as `PremadeCheckpointPath`. The game
rewrites `SANDBOX_0_0_0_.sbsB5` whenever the binary sector deserializes without objects, so nodes
that load the shared directory at the same time can read each other's half-written file and fail
with "Attempted to read past the end of the stream" (seen on the bench, P2-CHR-02, 2026-09-17).
`Cli/cluster.py` now gives each node its own copy (`Config.premade_world`); the harness could use
that path in its template and call `Cluster.refresh_premade_world` before each spawn.

## 5. linux-compat: route the server's ExitThreadSafe to Magnetar's graceful exit (T-0162)

Repo: the hub plugin `linux-compat` (LinuxCompat, public; cached as `GitHub/LinuxCompat-68cd6e19`).
`ClientPlugin.Patches.SystemAbstraction.MySandboxGameExitThreadSafePatch` is a Harmony prefix on
`MySandboxGame.ExitThreadSafe`:

```csharp
private static bool Prefix()
{
    MySandboxGame.IsExiting = true;
    Process.GetCurrentProcess().Kill();
    return false;
}
```

On a dedicated server it replaces Magnetar's own prefix (`ServerControl.SaveAndQuit`, exit 0), so
every in-process exit request - a drained cluster node, the World Authority, an admin `!stop` routed
through ExitThreadSafe - ends in SIGKILL (exit 137), with no plugin disposal and no log flush.
Caught on the bench 2026-09-17: gdb on a World Authority stopped in `kill(self, SIGKILL)` with
`MySandboxGameExitThreadSafePatch.Prefix` (plugin.dll token 0x06000095) as the calling frame, under
`WorldAuthorityPersistence.Update`. The live regression cluster logs the same 137 on every drain.

Proposed change: apply the patch on the client only (the game client's unload is what hangs), or
on the server return `true` / defer to Magnetar when `ServerControl` is present:

```csharp
private static bool Prepare() => !Sandbox.Engine.Platform.Game.IsDedicated;
```

The cluster plugins no longer depend on it: `ClusterRuntime.ExitProcess` leaves through libc
`_exit` with the decision's exit code (T-0162).

## 6. magnetar-hub: bump `direct-transport` to f7d0292 (T-0157) — DONE 2026-09-18

**Resolved.** Both hubs now pin `144a6e3c4217e239f4bbcd8a7fa675a4de81e17a` (tag 1.0.1,
2026-09-18 02:59), which contains c49afeb and f7d0292: `CometWorks/magnetar-hub`
`Plugins/DirectTransport.xml` (the `ServerPlugin` half, for nodes and the WA) and
`StarCpt/PluginHub` `Plugins/DirectTransport.xml` (the `ClientPlugin` half, for the game clients).
Verified 2026-09-19 10:3x. The cluster's defaults were switched back to the hub pin on the same day;
the original request is kept below for the record.


Repo: CometWorks/magnetar-hub, `Plugins/DirectTransport.xml`. It pins
`<Commit>2212094d8ae64cb7d015ad27cbaa0a88e4bc5b70</Commit>` (2026-07-08), checked 2026-09-17 14:2x.
The cluster plugins bind `Shared.Transport.UdpPeer2Peer.m_engineEvents`, which exists only from
direct-transport c49afeb ("Options and fixes (#4)", 2026-08-28). With the hub copy, wa-1 fails at
start: `Shared.Transport.UdpPeer2Peer has no field m_engineEvents`. The release packages therefore
still take direct-transport from the `~/ws/se/se1/direct-transport` dev folder (bench `versions.json`:
`directTransportSource 1.210.013-b0-works-1-3-gf7d0292`), which is what T-0157 wants to end.

Proposed change: set `<Commit>` to `f7d0292` (current CometWorks/direct-transport `main`, contains
c49afeb). After it is merged, a release package can take direct-transport from the hub, and T-0157's
first live start can go ahead.

## 7. Pulsar: the shared plugin cache is not safe for concurrent clients (T-0184)

Repo: `~/ws/se/Pulsar`. Two independent defects, either of which alone is survivable and which together
took out 29 of 64 clients at one fleet ramp on 2026-09-19 and 35 at another.

Our deployment runs up to 76 headless clients per host. Each gets its own HOME, and each reaches
ONE shared compile cache through a per-client symlink:

```
<client>/home/.config/Pulsar/Interim/GitHub  ->  ~/.cache/Pulsar/Legacy/GitHub
```

Sharing it is deliberate and worth keeping - a cold cache costs every client a full recompile.

### 7a. `PluginCache` records absolute paths into the walking client's own HOME

`Pulsar/Shared/Data/PluginCache.cs:73-101`, `IsValid` ends with

```csharp
Assets.All(asset => File.Exists(asset.Path) || Directory.Exists(asset.Path))
```

and `Path` is recorded as the path that was WALKED, i.e. through the symlink, in whichever client
happened to rebuild. Observed on `ai`:

```xml
<Path>/home/factory/cluster-runs/regression/client-58/home/.config/Pulsar/Interim/GitHub/LinuxCompat-68cd6e19/Bin</Path>
```

The moment `client-58`'s directory is re-seeded - or the fleet is resized so `client-58` does not
exist - the cache is invalid **for every client on the host**, though the files it names are all
present under the real cache root. Only LinuxCompat records assets (2 paths); the other four record
none and were immune, and one poisoned plugin is enough, because `IsValid` is all-or-nothing per
plugin and `dotnet-compat` is a CORE plugin whose failure aborts the client.

Proposed change: store asset paths RELATIVE to the cache root and resolve them against it in
`IsValid`. The cache then describes its own contents rather than the route one client took to them,
which is also what makes it portable between the per-client HOMEs it is already shared across.

### 7b. `CompileFromSource` has no lock, no staging directory and no atomic rename

`GitHubPlugin.CompileFromSource` does `Clear()` (`Directory.Delete(Bin)`), download, `File.Copy`
without overwrite, `WriteAllBytes` - directly into the live directory. With 7a poisoning the cache
for everyone at once, every client enters it simultaneously on the same directory. The losers die
on

```
The file '...Microsoft.Win32.Registry.dll' already exists
-> Failed to load core plugin 'dotnet-compat'
-> Pulsar cannot continue loading!
```

Proposed change: build into `<plugin>.tmp.<pid>` and publish with `rename(2)` (`Directory.Move`),
the whole operation under an `flock` on a lockfile beside the plugin directory. A client that loses
the race then blocks briefly and finds a complete directory, instead of deleting the one the winner
is writing into. `rename(2)` is atomic on the same filesystem, which the cache root always is.

7b alone would also fix the crash without 7a, and 7a alone would remove almost all of the
contention; both are worth having, since 7b protects against any other cause of a simultaneous
rebuild - a Pulsar version bump or a .NET runtime upgrade, both of which invalidate every entry
legitimately and did so on this host on 2026-09-19.

WHAT WE DID INSTEAD, so the harness workaround can be dropped when this lands: `regression/
endurance.py`'s `_CACHE_PROBE` (cluster-regression ffc0291) rewrites the per-client asset paths in
the shared manifest back onto the cache root before a ramp, and then re-checks exactly what
`IsValid` checks - PulsarVersion, `Bin/plugin.dll`, the `<Runtime>` string against
`dotnet --list-runtimes`, and every `<Path>`. It is a mitigation for 7a and does nothing about 7b.
