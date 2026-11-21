using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ClusterPlugin;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.Game.Multiplayer;
using VRage.Network;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// SE-1596: the Alt-F10 Entity List and PCU tabs, and the Entity List's row actions, for the whole cluster. The server
/// handlers of the vanilla events hand the request to <see cref="AdminMenuService"/> and answer the admin's client with
/// the same client events vanilla raises, once the other nodes answered. Vanilla's own admin check has already run:
/// these are [Server(SpaceMasterOnly / AdminOnly)] events.
///
/// A row action keeps vanilla for the ids this node owns and sends the rest to the cluster; the admin gets a chat line
/// with the outcome of those. Remove owner removes the owner's entities the same way; its identity part (close the
/// character, drop an identity with no blocks) stays this node's vanilla.
///
/// CLUSTER_ADMIN_WIDEN=0 restores vanilla, as for T-0180's widening.
/// </summary>
public static class AdminMenuClusterPatch
{
    private static readonly Lazy<
        Func<IMyEventOwner, Action<List<MyEntityList.MyEntityListInfoItem>>>
    > EntityListResponse = new(() =>
    {
        MethodInfo method =
            AccessTools.Method(
                typeof(MyGuiScreenAdminMenu),
                "EntityListResponse",
                new[] { typeof(List<MyEntityList.MyEntityListInfoItem>) }
            )
            ?? throw new MissingMethodException(
                typeof(MyGuiScreenAdminMenu).FullName,
                "EntityListResponse"
            );
        var handler =
            (Action<List<MyEntityList.MyEntityListInfoItem>>)
                Delegate.CreateDelegate(
                    typeof(Action<List<MyEntityList.MyEntityListInfoItem>>),
                    method
                );
        return _ => handler;
    });

    /// <summary>The admin who raised the event being handled, or 0 when it isn't a remote client's.</summary>
    private static ulong Admin()
    {
        if (!AdminMenuService.Active)
            return 0;
        MyEventContext context = MyEventContext.Current;
        return context.IsValid && !context.IsLocallyInvoked ? context.Sender.Value : 0;
    }

    [HarmonyPatch(typeof(MyGuiScreenAdminMenu), nameof(MyGuiScreenAdminMenu.EntityListRequest))]
    private static class EntityListRequest
    {
        [HarmonyPrefix]
        private static bool Prefix(MyEntityList.MyEntityTypeEnum selectedType)
        {
            ulong admin = Admin();
            if (admin == 0)
                return true;
            List<MyEntityList.MyEntityListInfoItem> local = MyEntityList.GetEntityList(
                selectedType
            );
            Answer(
                AdminMenuService.CollectListAsync(selectedType, local),
                admin,
                result =>
                {
                    AdminMenuService.ListStats stats = result.Stats;
                    Console.WriteLine(
                        $"[ClusterPlugin] admin entity list for {admin}: type={selectedType}"
                            + $" local={stats.Local} remote={stats.Remote} offline={stats.Offline}"
                            + $" rows={stats.Merged} replies={stats.Replies} bytes={stats.Bytes}"
                            + $" in {stats.Milliseconds}ms"
                            + (
                                stats.Missing.Length > 0
                                    ? " missing=" + string.Join(",", stats.Missing)
                                    : ""
                            )
                            + " (SE-1596)"
                    );
                    MyMultiplayer.RaiseStaticEvent(
                        EntityListResponse.Value,
                        result.Rows,
                        new EndpointId(admin)
                    );
                    Chat(admin, AdminMenuService.Incomplete(stats.Missing, stats.Truncated));
                },
                () =>
                    MyMultiplayer.RaiseStaticEvent(
                        EntityListResponse.Value,
                        local,
                        new EndpointId(admin)
                    )
            );
            return false;
        }
    }

    private static readonly Lazy<Action<float, long, Vector3D, bool>> CycleResponse = new(() =>
        (Action<float, long, Vector3D, bool>)
            Delegate.CreateDelegate(
                typeof(Action<float, long, Vector3D, bool>),
                AccessTools.Method(typeof(MyGuiScreenAdminMenu), "Cycle_Implementation")
                    ?? throw new MissingMethodException(
                        typeof(MyGuiScreenAdminMenu).FullName,
                        "Cycle_Implementation"
                    )
            )
    );

    /// <summary>SE-1687: Cycle Objects (First / Next / Previous) walks the whole cluster. The GPS order stays
    /// vanilla's: the client walks its own GPS list and never asks.</summary>
    [HarmonyPatch(
        typeof(MyGuiScreenAdminMenu),
        nameof(MyGuiScreenAdminMenu.CycleRequest_Implementation)
    )]
    private static class CycleRequest
    {
        [HarmonyPrefix]
        private static bool Prefix(
            MyEntityCyclingOrder order,
            bool reset,
            bool findLarger,
            float metricValue,
            long currentEntityId,
            CyclingOptions options
        )
        {
            ulong admin = Admin();
            if (admin == 0 || order == MyEntityCyclingOrder.Gps)
                return true;
            if (reset)
            {
                metricValue = float.MinValue;
                currentEntityId = 0;
                findLarger = false;
            }
            var step = AdminMenuService.CycleStep.Of(
                order,
                findLarger,
                metricValue,
                currentEntityId,
                options
            );
            AdminMenuService.Candidates local = AdminMenuService.CycleCandidates(
                step,
                AdminMenuService.CyclesHere
            );
            void Send(AdminMenuService.Pick pick) =>
                MyMultiplayer.RaiseStaticEvent(
                    (IMyEventOwner s) => CycleResponse.Value,
                    pick?.Value ?? (findLarger ? float.MaxValue : float.MinValue),
                    pick?.Id ?? 0,
                    pick == null ? Vector3D.Zero : new Vector3D(pick.X, pick.Y, pick.Z),
                    pick?.Station == true,
                    new EndpointId(admin)
                );
            Answer(
                AdminMenuService.CycleAsync(step, local),
                admin,
                result =>
                {
                    Send(result.Pick);
                    Chat(admin, AdminMenuService.CycleLine(result.Pick));
                    Chat(admin, AdminMenuService.Incomplete(result.Missing, Array.Empty<string>()));
                },
                () => Send(AdminMenuService.Choose(new[] { local }, step))
            );
            return false;
        }
    }

    /// <summary>SE-1687: the Trash Removal "Other" tab's Remove floating objects, Remove cargo bags and Stop entities
    /// run on every node. Vanilla still does this node's part.</summary>
    [HarmonyPatch]
    private static class Sweeps
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            new[]
            {
                "RemoveFloating_Implementation",
                "RemoveCargoBags_Implementation",
                "StopEntities_Implementation",
            }.Select(name =>
                AccessTools.Method(
                    "Sandbox.Game.Screens.AdminMenu.MyTrashOtherTabContainer:" + name
                )
            );

        [HarmonyPrefix]
        private static void Prefix(MethodBase __originalMethod)
        {
            ulong admin = Admin();
            if (admin == 0)
                return;
            var sweep = __originalMethod.Name switch
            {
                "RemoveFloating_Implementation" => AdminMenuService.Sweep.Floating,
                "RemoveCargoBags_Implementation" => AdminMenuService.Sweep.CargoBags,
                _ => AdminMenuService.Sweep.Stop,
            };
            Answer(
                AdminMenuService.SweepAsync(sweep),
                admin,
                text =>
                {
                    Console.WriteLine($"[ClusterPlugin] admin sweep by {admin}: {text} (SE-1687)");
                    Chat(admin, text);
                },
                () => { }
            );
        }
    }

    /// <summary>SE-1687: the Entity List's "distance from players" and the cycler's "biggest distance from players"
    /// count the players on every node, not only the characters this node has.</summary>
    [HarmonyPatch(typeof(MyEntityList), nameof(MyEntityList.GetPlayersMinimalDistance))]
    private static class ListDistance
    {
        [HarmonyPostfix]
        private static void Postfix(VRage.Game.Entity.MyEntity entity, ref float __result)
        {
            if (AdminMenuService.Active && entity != null)
                __result = AdminMenuService.NearestPlayer(
                    entity.PositionComp.GetPosition(),
                    __result
                );
        }
    }

    [HarmonyPatch(typeof(MyEntityCycling), nameof(MyEntityCycling.GetPlayerDistance))]
    private static class CycleDistance
    {
        [HarmonyPostfix]
        private static void Postfix(VRage.Game.Entity.MyEntity entity, ref float __result)
        {
            if (AdminMenuService.Active && entity != null)
                __result = AdminMenuService.NearestPlayer(
                    entity.PositionComp.GetPosition(),
                    __result
                );
        }
    }

    [HarmonyPatch(
        typeof(MyGuiScreenAdminMenu),
        nameof(MyGuiScreenAdminMenu.ProceedEntitiesAction_Implementation)
    )]
    private static class ProceedEntitiesAction
    {
        [HarmonyPrefix]
        private static void Prefix(List<long> entityIds, MyEntityList.EntityListAction action) =>
            Forward(entityIds, action);
    }

    [HarmonyPatch(
        typeof(MyGuiScreenAdminMenu),
        nameof(MyGuiScreenAdminMenu.ProceedEntity_Implementation)
    )]
    private static class ProceedEntity
    {
        [HarmonyPrefix]
        private static bool Prefix(long entityId, MyEntityList.EntityListAction action)
        {
            var ids = new List<long> { entityId };
            Forward(ids, action);
            return ids.Count > 0;
        }
    }

    [HarmonyPatch(
        typeof(MyGuiScreenAdminMenu),
        nameof(MyGuiScreenAdminMenu.RemoveOwner_Implementation)
    )]
    private static class RemoveOwner
    {
        [HarmonyPrefix]
        private static void Prefix(List<long> entityIds) =>
            Forward(entityIds, MyEntityList.EntityListAction.Remove);
    }

    /// <summary>Leaves the ids this node owns in <paramref name="ids"/> for vanilla and sends the others to the cluster.</summary>
    private static void Forward(List<long> ids, MyEntityList.EntityListAction action)
    {
        ulong admin = Admin();
        if (admin == 0 || ids == null)
            return;
        List<long> remote = AdminMenuService.TakeRemote(ids);
        if (remote.Count == 0)
            return;
        Answer(
            AdminMenuService.ForwardAsync(remote, action),
            admin,
            text =>
            {
                Console.WriteLine(
                    $"[ClusterPlugin] admin {action} by {admin} on {string.Join(",", remote)}: {text} (SE-1596)"
                );
                Chat(admin, text);
            },
            () => Chat(admin, "Admin " + action + ": the other servers could not be asked.")
        );
    }

    [HarmonyPatch(
        typeof(MyGuiScreenAdminMenu),
        nameof(MyGuiScreenAdminMenu.RequestFocusCameraOnLastKnownGridPos)
    )]
    private static class FocusCamera
    {
        [HarmonyPrefix]
        private static bool Prefix(long entityId)
        {
            ulong admin = Admin();
            if (
                admin == 0
                || MyEntities.EntityExists(entityId)
                || !AdminMenuService.TryKnown(entityId, out Vector3D position, out _)
            )
                return true;
            MyMultiplayer.RaiseStaticEvent(
                (IMyEventOwner s) => MyGuiScreenAdminMenu.FocusCameraOnLastKnownGridPos,
                position,
                entityId,
                new EndpointId(admin)
            );
            return false;
        }
    }

    [HarmonyPatch(
        typeof(MyGuiScreenAdminMenu),
        nameof(MyGuiScreenAdminMenu.RequestPlayerGridsUpdate)
    )]
    private static class PlayerGrids
    {
        [HarmonyPrefix]
        private static bool Prefix(long identityId)
        {
            ulong admin = Admin();
            if (admin == 0)
                return true;
            List<AdminMenuService.GridPcu> local = AdminMenuService.PlayerGrids(
                identityId,
                ownedOnly: true
            );
            Answer(
                AdminMenuService.CollectPlayerGridsAsync(identityId, local),
                admin,
                result =>
                {
                    MyMultiplayer.RaiseStaticEvent(
                        (IMyEventOwner s) => MyGuiScreenAdminMenu.RefreshPlayersGrids,
                        identityId,
                        result.Names,
                        result.Ids,
                        result.TotalPcu,
                        new EndpointId(admin)
                    );
                    Chat(admin, AdminMenuService.Incomplete(result.Missing, Array.Empty<string>()));
                },
                () =>
                {
                    var (names, ids, total) = AdminMenuService.MergeGrids(
                        local,
                        Array.Empty<(string, AdminMenuService.GridPcu)>(),
                        Array.Empty<AdminMenuService.OfflineGrid>()
                    );
                    MyMultiplayer.RaiseStaticEvent(
                        (IMyEventOwner s) => MyGuiScreenAdminMenu.RefreshPlayersGrids,
                        identityId,
                        names,
                        ids,
                        total,
                        new EndpointId(admin)
                    );
                }
            );
            return false;
        }
    }

    [HarmonyPatch(typeof(MyGuiScreenAdminMenu), nameof(MyGuiScreenAdminMenu.RequestPCUInfoUpdate))]
    private static class PcuInfo
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            ulong admin = Admin();
            if (admin == 0)
                return true;
            int[] local = AdminMenuService.PcuHere();
            int limit = Sandbox.Game.World.MySession.Static.Settings.TotalPCU;
            void Send(int[] pcu) =>
                MyMultiplayer.RaiseStaticEvent(
                    (IMyEventOwner s) => MyGuiScreenAdminMenu.RefreshPCUInfo,
                    limit,
                    pcu[0],
                    pcu[1],
                    pcu[2],
                    pcu[3],
                    pcu[4],
                    new EndpointId(admin)
                );
            Answer(
                AdminMenuService.CollectPcuAsync(local),
                admin,
                result =>
                {
                    Send(result.Sums);
                    Chat(admin, AdminMenuService.Incomplete(result.Missing, Array.Empty<string>()));
                },
                () => Send(local)
            );
            return false;
        }
    }

    /// <summary>Runs <paramref name="answer"/> on the game thread when the task is done, or <paramref name="fallback"/>
    /// (this node's own answer) when it failed.</summary>
    private static void Answer<T>(Task<T> task, ulong admin, Action<T> answer, Action fallback) =>
        task.ContinueWith(done =>
            MySandboxGame.Static?.Invoke(
                () =>
                {
                    try
                    {
                        if (done.Status == TaskStatus.RanToCompletion)
                        {
                            answer(done.Result);
                            return;
                        }
                        Console.WriteLine(
                            $"[ClusterPlugin] admin menu request of {admin} failed: {done.Exception?.GetBaseException()} (SE-1596)"
                        );
                        fallback();
                        Chat(
                            admin,
                            "Admin menu: the other servers could not be asked, this server's answer only."
                        );
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine(
                            $"[ClusterPlugin] admin menu answer to {admin} failed: {exception} (SE-1596)"
                        );
                    }
                },
                "ClusterAdminMenu"
            )
        );

    private static void Chat(ulong admin, string text)
    {
        if (!string.IsNullOrEmpty(text))
            (MyMultiplayer.Static as MyDedicatedServerBase)?.SendChatMessageToPlayer(text, admin);
    }
}
