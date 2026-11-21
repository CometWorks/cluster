using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace ClusterPlugin;

internal static partial class AdminSafeZones
{
    // MagnetarMod sends this server-local message after confirming the destination character.
    private const long TeleportCompleted = 0x4D47545000000001;
    private const string PadSubtype = "MagnetarMod_PlayerTeleporter";

    private sealed record Point(double X, double Y, double Z);

    private sealed record Anchor(long GridId, Point Position);

    private static readonly Anchor NoAnchor = new(0, null);
    private static volatile Anchor s_desired;
    private static Anchor s_installed;
    private static Lobby s_lobby; // World Authority, persisted with its zone set under Gate.

    internal static void ReceiveLobbyAnchor(JsonElement value)
    {
        s_desired = value.ValueKind == JsonValueKind.Null ? NoAnchor : value.Deserialize<Anchor>();
    }

    private static void ReconcileLobbyZone()
    {
        var anchor = s_desired;
        if (anchor == null || ClusterRuntime.IsAuthorityFenced)
            return;
        lock (Gate)
        {
            if (anchor == s_installed && (s_lobby == null || s_zones.ContainsKey(s_lobby.Zone)))
                return;
            if (anchor == NoAnchor)
            {
                s_installed = anchor;
                if (s_lobby == null)
                    return;
                s_zones.Remove(s_lobby.Zone);
                s_lobby = null;
            }
            else
            {
                var position = new Vector3D(
                    anchor.Position.X,
                    anchor.Position.Y,
                    anchor.Position.Z
                );
                long id = s_lobby?.Zone ?? MyEntityIdentifier.AllocateId();
                var zone = s_zones.TryGetValue(id, out var xml) ? FromXml(xml) : null;
                if (zone != null && s_lobby.Grid == anchor.GridId)
                {
                    s_installed = anchor;
                    return;
                }
                zone ??= new MyObjectBuilder_SafeZone
                {
                    EntityId = id,
                    DisplayName = "Cluster lobby",
                    PersistentFlags = MyPersistentEntityFlags2.InScene,
                    Enabled = true,
                    Shape = MySafeZoneShape.Sphere,
                    Radius = MySafeZone.MAX_RADIUS,
                    AccessTypePlayers = MySafeZoneAccess.Blacklist,
                    AccessTypeGrids = MySafeZoneAccess.Blacklist,
                    AccessTypeFactions = MySafeZoneAccess.Blacklist,
                    AccessTypeFloatingObjects = MySafeZoneAccess.Blacklist,
                };
                zone.PositionAndOrientation = new MyPositionAndOrientation(
                    position,
                    Vector3.Forward,
                    Vector3.Up
                );
                s_zones[id] = ToXml(zone);
                s_lobby = new Lobby(anchor.GridId, id, s_lobby?.Travel ?? []);
                s_installed = anchor;
            }
            s_version++;
            GlobalStateChannel.Publish(Subsystem, Snapshot());
        }
        ClusterRuntime.RequestGlobalSave();
    }

    internal static bool ChangeLobbyAccess(ref Lobby lobby, Request request)
    {
        if (
            request.Op != "Travel"
            || lobby == null
            || request.Id != lobby.Grid
            || request.Player == 0
            || request.Trip is not { Generation: > 0, Milliseconds: >= 0 } trip
            || !Newer(trip, lobby.Travel.GetValueOrDefault(request.Player))
        )
            return false;

        // Never mutate a set that a snapshot or the game thread may already be reading.
        var travel = new Dictionary<ulong, Passage>(lobby.Travel) { [request.Player] = trip };
        lobby = lobby with { Travel = travel };
        return true;
    }

    private static bool Newer(Passage trip, Passage previous) =>
        previous == null
        || (trip.Generation, trip.Milliseconds).CompareTo(
            (previous.Generation, previous.Milliseconds)
        ) > 0;

    // Keep confirmed moves until the WA's durable command ledger acknowledges them.
    // Each outstanding move has one file, including the operation id used for every retry.
    private static string s_travelDirectory;

    private static void InstallLobbyTravel()
    {
        var root = Environment.GetEnvironmentVariable("CLUSTER_DATA_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            s_travelDirectory = Path.Combine(root, "cluster", "lobby-travel");
            Directory.CreateDirectory(s_travelDirectory);
            Outbox.AddRange(
                Directory
                    .EnumerateFiles(s_travelDirectory, "*.json")
                    .Select(path => JsonSerializer.Deserialize<Queued>(File.ReadAllBytes(path)))
                    .OrderBy(q => q.Request.Trip.Generation)
                    .ThenBy(q => q.Request.Trip.Milliseconds)
            );
        }
        Sandbox.ModAPI.MyAPIUtilities.Static.RegisterMessageHandler(
            TeleportCompleted,
            OnTeleportCompleted
        );
    }

    private static void AcknowledgeTravel(Queued queued)
    {
        if (queued.Request.Op == "Travel" && s_travelDirectory != null)
            File.Delete(Path.Combine(s_travelDirectory, queued.OperationId + ".json"));
    }

    private static void OnTeleportCompleted(object message)
    {
        if (
            !Active
            || message is not MyTuple<ulong, long, long, long> trip
            || trip.Item1 == 0
            || trip.Item2 == 0
            || MySession.Static?.Players.TryGetIdentityId(trip.Item1) != trip.Item2
            || !TryPad(trip.Item3, out var source)
            || !TryPad(trip.Item4, out var destination)
        )
            return;

        lock (Gate)
        {
            if (s_newest?.Lobby is not { } lobby || !TryZone(lobby, out var zone))
                return;
            bool fromLobby = zone.Contains(source.PositionComp.GetPosition());
            bool toLobby = zone.Contains(destination.PositionComp.GetPosition());
            if (fromLobby == toLobby)
                return;
            var passage = new Passage(
                ClusterRuntime.AuthorityGeneration,
                ClusterRuntime.CatalogClusterMilliseconds(),
                !toLobby
            );
            var queued = new Queued(
                new Request("Travel", lobby.Grid, Player: trip.Item1, Trip: passage),
                Guid.NewGuid()
            );
            if (s_travelDirectory != null)
            {
                string path = Path.Combine(s_travelDirectory, queued.OperationId + ".json");
                File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(queued));
                File.Move(path + ".tmp", path);
            }
            Outbox.Add(queued);
        }
    }

    private static bool TryPad(long id, out MyCubeBlock block) =>
        MyEntities.TryGetEntityById(id, out block)
        && !block.MarkedForClose
        && block.BlockDefinition.Id.SubtypeName == PadSubtype;

    private static bool TryZone(Lobby lobby, out MySafeZone zone) =>
        MyEntities.TryGetEntityById(lobby.Zone, out zone)
        && !zone.MarkedForClose
        && zone.SafeZoneBlockId == 0;

    // Regular node, game thread. The native zone applies its normal outward force to these bodies.
    private static long s_entryZone;
    private static int s_lastEntryFrame;

    private sealed record EntryFilter(long Zone, HashSet<long> Bodies);

    private static volatile EntryFilter s_filter = new(0, []);
    private static readonly HashSet<ulong> InsideLastSample = [];
    private static readonly HashSet<ulong> InsideThisSample = [];

    internal static bool DeniesEntry(long zone, long body)
    {
        var filter = s_filter;
        return zone == filter.Zone && filter.Bodies.Contains(body);
    }

    private static void UpdateLobbyEntry()
    {
        int frame = MySession.Static.GameplayFrameCounter;
        if (unchecked((uint)(frame - s_lastEntryFrame)) < 8)
            return;
        s_lastEntryFrame = frame;
        lock (Gate)
        {
            var lobby = s_newest?.Lobby;
            if (s_entryZone != (lobby?.Zone ?? 0))
                InsideLastSample.Clear();
            s_entryZone = lobby?.Zone ?? 0;
            if (lobby == null || !TryZone(lobby, out var zone) || !zone.Enabled)
            {
                InsideLastSample.Clear();
                if (s_filter.Zone != 0)
                    s_filter = new(0, []);
                return;
            }
            var bodies = new HashSet<long>();
            InsideThisSample.Clear();
            foreach (var player in MySession.Static.Players.GetOnlinePlayers())
            {
                ulong user = player.Id.SteamId;
                if (
                    player.Character is not { IsDead: false } character
                    || PromotionService.HasLevel(user, MyPromoteLevel.Admin)
                )
                    continue;
                var passage = lobby.Travel.GetValueOrDefault(user);
                // Apply local completions immediately while waiting for the durable answer.
                foreach (var queued in Outbox)
                    if (
                        queued.Request.Id == lobby.Grid
                        && queued.Request.Player == user
                        && queued.Request.Trip is { } local
                        && Newer(local, passage)
                    )
                        passage = local;
                if (
                    passage?.Departed != true
                    || !zone.Contains(character.PositionComp.GetPosition())
                )
                    continue;
                InsideThisSample.Add(user);
                // MagnetarMod confirms arrival three frames after moving the character.
                if (!InsideLastSample.Contains(user))
                    continue;
                var body = character.GetTopMostParent();
                if (ClusterRuntime.IsFrozen(body.EntityId))
                    continue;
                // A seat attached to a station cannot be pushed. Release only the denied rider.
                if (
                    body is MyCubeGrid seatedGrid
                    && character.Parent is MyCockpit seat
                    && MyCubeGridGroups
                        .Static.Physical.GetGroupNodes(seatedGrid)
                        .Any(g => g.IsStatic)
                )
                {
                    seat.RemovePilot();
                    bodies.Add(character.EntityId);
                    continue;
                }
                bodies.Add(body.EntityId);
                if (
                    body is MyCubeGrid grid
                    && MyCubeGridGroups.Static?.Mechanical.GetGroup(grid) is { } group
                )
                    foreach (var node in group.Nodes)
                        bodies.Add(node.NodeData.EntityId);
            }
            InsideLastSample.Clear();
            InsideLastSample.UnionWith(InsideThisSample);
            if (s_filter.Zone != lobby.Zone || !s_filter.Bodies.SetEquals(bodies))
                s_filter = new(lobby.Zone, bodies);
        }
    }
}
