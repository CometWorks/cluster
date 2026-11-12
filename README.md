# Space Engineers cluster plugins

Magnetar plugins that turn stock Space Engineers dedicated servers into the nodes of a
clustered world: one **World Authority** (`cluster-wa`) and any number of **regular nodes**
(`cluster-node`), coordinated by the [cluster Gateway](ClusterRegistry/README.md)
over the [Direct Transport](https://github.com/CometWorks/direct-transport) plugin. The plugins run
on the unmodified `DedicatedServer64` binaries under [Magnetar](https://github.com/CometWorks/magnetar).

## Recommended deployment: Quasar

Use [Quasar](https://github.com/CometWorks/quasar) to automatically provision and manage a
cluster. Its guided setup installs the Dedicated Server and Magnetar, stages a verified
compatible cluster release and required plugins, prepares the world and credentials, and
deploys the configuration to the selected host machines.

1. Install Quasar using its [Quick Start](https://github.com/CometWorks/quasar/blob/main/Docs/QuickStart.md).
2. Choose **Create Cluster** on the Dashboard, then **Create and deploy**. Enroll or select
   connected host machines and choose the world, configuration profile, ports and node counts.
3. Complete provisioning, then use **Start** in the cluster controls when ready. Setup
   activates the deployment in a stopped state.

See Quasar's [guided cluster setup](https://github.com/CometWorks/quasar/blob/main/Docs/ClusterGuidedSetupPlan.md)
for host requirements and compatibility checks, and the
[cluster overview](https://github.com/CometWorks/quasar/blob/main/Docs/ClusterOverview.md)
for day-to-day operation.

For development, testing or an independently operated cluster, see
[Manual deployment and development](Docs/ManualDeployment.md). It covers prerequisites,
building, Magnetar registration, CLI controls, release packaging and debugging.

## Repository layout

| Project | Role |
| --- | --- |
| `ClusterNodePlugin` | Regular node hosting partitions (`cluster-node`) |
| `WorldAuthorityPlugin` | World Authority (`cluster-wa`) |
| `ClientDiagnosticsPlugin` | Optional F11 client diagnostics |
| `Shared` | Cluster runtime, seam holders and Harmony patches shared by the role plugins |
| `ClusterRegistry`, `GatewayRs` | Gateway, registry and Rust data plane |
| `ClusterRegistry.AdminContract`, `ClusterRegistry.Cli` | Versioned administration contract and CLI |
| `Tools/MagnetarWorld` | World partitioning, verification and save conversion |
| `Cli`, `Deploy`, `Build` | Manual operation, registration and packaging tools |
| `Test` | Cluster scenario suite and focused checks |

## Documentation

- [Manual deployment and development](Docs/ManualDeployment.md)
- [Client diagnostics](Docs/Gateway/ClientDiagnostics.md)
- [Offline compensation and call-ups](Docs/OfflinePartitions.md) — implementation draft and acceptance gaps
- [Managed deployment preparation and executor leases](Docs/Gateway/ManagedDeployment.md)
- [Release packages and bench archives](Docs/Gateway/DeploymentArchive.md)
- [Versioning](Docs/Versioning.md)
- [Scenario suite](Test/README.md)

## License

CometWorks Clustering (Dynamic Mashing)
Copyright (C) 2026 CometWorks (https://github.com/CometWorks)

The [CometWorks Independent Extension Exception](EXCEPTION.md) permits
proprietary terms only for qualifying Independent Extensions. A plugin label,
wrapper or separate process does not exempt covered core code or internal
patches. Public contract code is also available under MIT as specified in the
[licence guide](LICENSING.md).
Applicable AGPL source obligations remain in force.

[Alternative licensing](LICENSING.md#alternative-licensing) remains available by
separate written agreement with the relevant copyright holders.

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU Affero General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU Affero General Public License for more details.

You should have received a copy of the GNU Affero General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
