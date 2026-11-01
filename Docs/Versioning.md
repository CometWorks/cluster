# Versioning

One version number covers every C# project in this repository, the release package and the
compiled plugins, so a build can always be traced back to a single commit.

`Version.Build.props` is the single source:

```xml
<Version>0.1.11</Version>
```

`Directory.Build.props` imports it at the repository root, so every project inherits `Version`,
`AssemblyVersion` and `FileVersion` from it. **Do not add version properties to individual
projects** — check the effective value instead:

```bash
dotnet msbuild <project>.csproj -getProperty:AssemblyVersion
```

The number is three-part (SemVer). The assemblies come out as `0.1.11.0`, because .NET pads the
fourth field, while the release tag stays `v0.1.11` and the package `ClusterForLinux-0.1.11.tar.gz`
— `Build/release.sh` and `.github/workflows/release.yml` read `<Version>` out of
`Version.Build.props` and would carry a fourth field into both names.

## Which projects

All of them, including the ones outside `Clustering.sln` (`Tools/`, `Test/ClusterTestPlugin/`):
MSBuild imports the nearest `Directory.Build.props` walking up from each project directory, and
the repository root holds the only one. `Shared/Shared.shproj` is a shared project compiled into
the two plugins, so it has no version of its own.


## Bumping the version

1. Edit `<Version>` in `Version.Build.props`.
2. Update the `#if !LOCAL_BUILD` block of `ClusterNodePlugin/Plugin.cs` and
   `WorldAuthorityPlugin/Plugin.cs` to the same version, four-part:

   ```csharp
   #if !LOCAL_BUILD
   using System.Reflection;

   [assembly: AssemblyVersion("0.1.11.0")]
   [assembly: AssemblyFileVersion("0.1.11.0")]
   #endif
   ```

   Magnetar compiles the plugin sources itself without the `LOCAL_BUILD` constant the projects
   define, so these literals are what a Magnetar build stamps; a local `dotnet build` takes the
   msbuild properties instead and the `#if` block is compiled out. Nothing enforces that the two
   agree, so they are bumped together.

Bumping `<Version>` in a pull request is what cuts a release; see
[Manual deployment: Release](ManualDeployment.md#release) for how the workflow decides to publish.

## Magnetar PluginSdk

The plugins compile against the `PluginSdk.dll` of one Magnetar release: the tag in
`.github/workflows/release.yml` and `Build/fetch-magnetar.sh` (also named in
`Build/release-README.md` and `Docs/Gateway/DeploymentArchive.md`), and the source commit in
`Build/pr-magnetar-sdk-revision.txt` for pull request builds. `Build/release.sh` records that
SDK's contract version (`[assembly: AssemblyMetadata("PluginSdkContract", "major.minor")]`) as
`pluginSdkContract` in `cli/deployment-capabilities.json`, next to the file's `pluginSdkSha256`.
Quasar accepts any Magnetar whose contract has the same major and an equal or higher minor, so
a Magnetar release that keeps the contract needs no cluster release. Move the SDK pin when:

- the cluster starts to use PluginSdk API from a later contract; raise `Major`/`Minor` in
  `Shared/Cluster/PluginSdkContract.cs` (the plugins' and `--check-plugin-sdk`'s minimum) with it,
- Magnetar raises the contract major, or
- Magnetar's `PluginSdk/Clustering/PluginProtocol.cs` changes (`Build/verify-plugin-protocol.py`).

An SDK without a contract (Magnetar v2.4.3.6 and older) leaves only `pluginSdkSha256`, which
Quasar compares byte for byte. While the pin is on such a release, every Magnetar release still
needs a cluster release built against it.

## Exception

`ClusterRegistry.AdminContract` overrides `<Version>` with the version of its NuGet package
(`CometWorks.ClusterGateway.AdminContract`), which a contract change bumps on its own schedule
(`Docs/Gateway/AdminAPI.md`). Its assembly version still comes from `Version.Build.props`.
