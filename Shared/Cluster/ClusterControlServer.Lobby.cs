using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Xml;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClusterPlugin;

internal sealed partial class ClusterControlServer
{
    private sealed record LobbyRequest(
        string Mode,
        string Node,
        long Epoch,
        long GridEntityId,
        ulong Partition,
        double X,
        double Y,
        double Z,
        byte[] Blueprint
    );

    private sealed record LobbyResult(
        string Status,
        string Reason,
        long GridEntityId = 0,
        ulong Partition = 0,
        uint Generation = 0,
        string Name = null,
        double X = 0,
        double Y = 0,
        double Z = 0
    );

    private static readonly JsonSerializerOptions LobbyJson = new(JsonSerializerDefaults.Web);
    private const int MaximumBlueprintBytes = 1024 * 1024;

    // The Registry reserves this much clearance around the requested station pivot on every node.
    private const double MaximumStationRadiusMetres = 1000;

    private static void HandleLobbyAnchor(HttpListenerContext context)
    {
        // The Gateway sends a known-length JSON request with at most one base64-encoded bp.sbc.
        if (
            context.Request.ContentLength64 <= 0
            || context.Request.ContentLength64 > 2 * MaximumBlueprintBytes
        )
        {
            WriteText(context, 413, "Lobby request requires a bounded content length.");
            return;
        }
        LobbyRequest request;
        try
        {
            request = JsonSerializer.Deserialize<LobbyRequest>(
                context.Request.InputStream,
                LobbyJson
            );
        }
        catch (JsonException)
        {
            WriteText(context, 400, "Invalid lobby request.");
            return;
        }

        var result = Invoke(() => ApplyLobbyAnchor(request));
        Write(
            context,
            200,
            "application/json",
            JsonSerializer.SerializeToUtf8Bytes(result, LobbyJson)
        );
    }

    private static LobbyResult ApplyLobbyAnchor(LobbyRequest request)
    {
        if (request == null || request.Mode is not ("inspect" or "create"))
            return new("invalid", "Specify inspect or create.");
        if (
            request.Epoch <= 0
            || request.Epoch != ClusterRuntime.RegistryNodeEpoch
            || request.Node != Environment.GetEnvironmentVariable("CLUSTER_NODE_ID")
            || !ClusterRuntime.IsServing
            || ClusterRuntime.IsWorldAuthority
            || m_persistence.RegistryHeld
        )
            return new("conflict", "The selected node incarnation is no longer serving.");
        if (request.GridEntityId < 0 || (request.GridEntityId == 0 && request.Partition == 0))
            return new("invalid", "Specify a grid or an observed partition.");

        MyCubeGrid grid;
        if (request.Mode == "create")
        {
            if (
                request.GridEntityId == 0
                || request.Blueprint is not { Length: > 0 and <= MaximumBlueprintBytes }
                || !double.IsFinite(request.X)
                || !double.IsFinite(request.Y)
                || !double.IsFinite(request.Z)
            )
                return new(
                    "invalid",
                    "Creation requires a reserved grid ID, a finite position and a blueprint up to 1 MiB."
                );
            if (
                !MyEntities.TryGetEntityById(
                    request.GridEntityId,
                    out MyEntity existing,
                    allowClosed: true
                )
            )
            {
                try
                {
                    grid = CreateLobbyStation(request);
                }
                catch (Exception invalid) when (invalid is InvalidDataException or XmlException)
                {
                    return new("invalid", invalid.Message);
                }
            }
            else if (existing is MyCubeGrid present)
            {
                grid = present;
                if (
                    Vector3D.DistanceSquared(
                        grid.PositionComp.GetPosition(),
                        new(request.X, request.Y, request.Z)
                    ) > 1
                )
                    return new("conflict", "The reserved grid is at a different position.");
            }
            else
                return new("conflict", "The reserved identity belongs to another entity.");
        }
        else if (request.GridEntityId > 0)
        {
            if (!MyEntities.TryGetEntityById(request.GridEntityId, out grid))
                return new("missing", "The selected grid is absent on this node.");
        }
        else
        {
            var candidates = MyEntities
                .GetEntities()
                .OfType<MyCubeGrid>()
                .Where(x =>
                    x.IsStatic
                    && !x.MarkedForClose
                    && m_partitions.Holder(x.EntityId) == request.Partition
                )
                .Take(2)
                .ToArray();
            if (candidates.Length == 0)
                return new("missing", "No station found in this partition.");
            if (candidates.Length != 1)
                return new("invalid", "Multiple stations are present. Select an explicit grid.");
            grid = candidates[0];
        }

        if (
            grid == null
            || grid.Closed
            || grid.MarkedForClose
            || !grid.IsStatic
            || grid.GetTopMostParent() != grid
        )
            return new("invalid", "The anchor must be a live, static station grid.");
        var position = grid.PositionComp.GetPosition();
        ulong partition = m_partitions.Holder(grid.EntityId);
        if (request.Partition != 0 && partition != request.Partition)
            return new("conflict", "The station no longer belongs to the observed partition.");
        uint generation = m_persistence.OwnedGeneration(partition);
        string status = "pending",
            reason = "Waiting for the station's owned partition and registered save.";
        if (
            generation != 0
            && !m_persistence.ClaimPending(partition)
            && !m_persistence.Offlining(partition)
            && !ClusterRuntime.IsFrozen(grid.EntityId)
            && m_persistence.LobbyAnchorSaved(grid.EntityId, partition, generation)
        )
        {
            status = "ready";
            reason = null;
        }
        return new(
            status,
            reason,
            grid.EntityId,
            partition,
            generation,
            grid.DisplayName,
            position.X,
            position.Y,
            position.Z
        );
    }

    private static MyCubeGrid CreateLobbyStation(LobbyRequest request)
    {
        using var input = new MemoryStream(request.Blueprint, writable: false);
        // Keen accepts gzipped serializers too. Only the validated, bounded XML document is an input here.
        using (
            var reader = XmlReader.Create(
                input,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaximumBlueprintBytes,
                }
            )
        )
        {
            reader.MoveToContent();
            if (reader.LocalName != "Definitions")
                throw new InvalidDataException("Expected a Space Engineers grid blueprint.");
            while (reader.Read())
                if (reader.Depth > 128)
                    throw new InvalidDataException("Blueprint XML is too deeply nested.");
        }
        input.Position = 0;
        if (
            !MyObjectBuilderSerializerKeen.DeserializeXML(
                input,
                out MyObjectBuilder_Definitions definitions
            )
            || definitions?.ShipBlueprints is not { Length: 1 }
            || definitions.ShipBlueprints[0]?.CubeGrids is not { Length: 1 } grids
        )
            throw new InvalidDataException("The blueprint must contain a single station grid.");
        var builder = grids[0];
        if (builder == null || !builder.IsStatic || builder.CubeBlocks is not { Count: > 0 })
            throw new InvalidDataException("The blueprint must contain a nonempty static grid.");
        foreach (var block in builder.CubeBlocks)
        {
            if (
                block == null
                || !MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.GetId(), out _)
            )
                throw new InvalidDataException(
                    "The cluster is missing a blueprint block definition: " + block?.GetId()
                );
            if (block is MyObjectBuilder_Cockpit { Pilot: not null })
                throw new InvalidDataException(
                    "The station blueprint contains a seated character."
                );
        }

        var position = new Vector3D(request.X, request.Y, request.Z);
        var pose =
            builder.PositionAndOrientation
            ?? new MyPositionAndOrientation(position, Vector3.Forward, Vector3.Up);
        pose.Position = position;
        var world = pose.GetMatrix();
        if (!world.IsValid() || !world.GetOrientation().IsRotation())
            throw new InvalidDataException("Invalid station orientation.");
        BoundingBoxD local = builder.CalculateBoundingBox();
        for (int corner = 0; corner < 8; corner++)
        {
            var offset = local.GetCorner(corner);
            if (
                !offset.IsValid()
                || offset.LengthSquared() > MaximumStationRadiusMetres * MaximumStationRadiusMetres
            )
                throw new InvalidDataException(
                    "All station blocks must fit within 1 km of the grid origin."
                );
        }
        var bounds = BoundingBoxD.CreateInvalid();
        local.TransformFast(ref world, ref bounds);
        if (
            !bounds.Min.IsValid()
            || !bounds.Max.IsValid()
            || !MySession.IsInsideWorld(bounds.Min)
            || !MySession.IsInsideWorld(bounds.Max)
        )
            throw new InvalidDataException("The station extends beyond the world boundary.");
        var nearby = new List<MyEntity>();
        MyEntities.GetTopMostEntitiesInBox(ref bounds, nearby);
        if (nearby.Count != 0)
            throw new InvalidDataException(
                "The station's bounds intersect an existing world entity."
            );

        // Preserve all internal block references using the same cluster-aware ID remapper as a grid paste.
        var remapper = new MyEntityIdRemapHelper();
        if (builder.EntityId != 0)
            remapper.GetRemapInfo().Add(builder.EntityId, request.GridEntityId);
        builder.Remap(remapper);
        builder.EntityId = request.GridEntityId;
        builder.PositionAndOrientation = pose;
        builder.LocalPositionAndOrientation = null;
        builder.LinearVelocity = Vector3.Zero;
        builder.AngularVelocity = Vector3.Zero;
        builder.CreatePhysics = true;
        builder.PersistentFlags |= MyPersistentEntityFlags2.InScene;
        var grid = MyEntities.CreateFromObjectBuilder(builder, fadeIn: false) as MyCubeGrid;
        if (
            grid == null
            || grid.BlocksCount != builder.CubeBlocks.Count
            || grid.EntityId != request.GridEntityId
        )
        {
            grid?.Close();
            throw new InvalidDataException("The server could not build the complete station.");
        }
        MyEntities.Add(grid);
        return grid;
    }
}

internal sealed partial class PartitionPersistence
{
    // One mutation can be outstanding per cluster. Hold its Final until the writer confirms catalog registration.
    private PendingSave m_lobbySave;
    private (long Grid, ulong Partition, uint Generation) m_lobbySaveKey;
    private bool m_lobbySaveConfirmed;

    internal bool LobbyAnchorSaved(long grid, ulong partition, uint generation)
    {
        var key = (grid, partition, generation);
        if (m_lobbySaveKey != key)
        {
            m_lobbySave = null;
            m_lobbySaveKey = key;
            m_lobbySaveConfirmed = false;
        }
        if (m_lobbySaveConfirmed)
            return true;
        if (m_lobbySave != null)
        {
            if (!m_lobbySave.Completed.IsSet)
                return false;
            m_lobbySaveConfirmed = m_lobbySave.Succeeded && !m_lobbySave.LocalOnly;
            m_lobbySave = null;
            if (m_lobbySaveConfirmed)
                return true;
        }
        var station = m_partitions.Peek(partition);
        if (!station.HasPending && !m_fenced && !m_localStop && !GetState(partition).Pending)
            m_lobbySave = QueueCapture(station, "Final", waitForSlot: false);
        return false;
    }
}
