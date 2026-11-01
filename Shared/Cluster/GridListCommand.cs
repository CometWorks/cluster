using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using PluginSdk.Clustering;
using PluginSdk.Commands;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// SE-810: !ess grids list for a cluster, in place of Essentials' own, which sees neither other nodes' grids nor offline
/// partitions unless it knows the cluster. A plugin registering a command later wins, so this registers once the session is
/// up, only where Essentials is loaded, and says the same thing under the same options, read from Essentials.cfg:
/// MarkerShowPosition for the GPS markers, UtilityShowPosition for the positions. Every node lists the player's grids it
/// owns; the gateway has those in offline partitions, from the freeze, so nothing is loaded. !grids list is the same command
/// under the name players know from Torch's Essentials.
/// </summary>
[CommandRoot("ess", "Essentials", "Essentials server commands")]
public sealed class GridListCommand : CommandModule
{
	private const string Plugin = "cluster-node";
	private const string Topic = "grids-list";
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
	private static bool m_registered;
	private static IDisposable m_handler;

	internal static void Update()
	{
		if (m_registered || !ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority
			|| Sandbox.Game.World.MySession.Static?.Ready != true || PluginServices.Instance == null || ServerCommands.Registrar == null) return;
		m_registered = true;
		if (!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "Essentials")) return;
		m_handler = PluginServices.Instance.RegisterHandler(Plugin, Topic, OnList);
		ServerCommands.Register(typeof(GridListCommand).Assembly, typeof(GridListCommand), typeof(GridListAlias));
		Console.WriteLine("[ClusterPlugin] !ess grids list taken over from Essentials, !grids list added");
	}

	private sealed record Line(long Id, string Name, string Size, int Blocks, double X, double Y, double Z, bool Offline = false);

	[Command("grids list", "List your grids, on any server of the cluster and in areas nobody is near.")]
	[Permission(MyPromoteLevel.None)]
	public void GridsList() => List(Context);

	internal static void List(CommandContext context)
	{
		if (context.Caller.IsConsole || context.Caller.IdentityId == 0)
		{
			context.Respond("Console cannot list player-owned grids.");
			return;
		}
		long identity = context.Caller.IdentityId;
		string caller = context.Caller.Name;
		Task<PluginBroadcastResult> online = AskNodes(identity);
		Task<(List<Line> Grids, int Unknown)> offline = OfflineGrids(identity);
		Task.WhenAll(online.ContinueWith(_ => { }), offline.ContinueWith(_ => { })).ContinueWith(_ =>
		{
			List<Line> grids = null;
			bool nodeMissing = false;
			if (online.Status == TaskStatus.RanToCompletion && online.Result?.Nodes != null)
				foreach (PluginResult answer in online.Result.Nodes.Values)
					if (answer.Code == PluginResultCode.Success)
						(grids ??= new List<Line>()).AddRange(JsonSerializer.Deserialize<Line[]>(answer.Payload ?? Encoding.UTF8.GetBytes("[]")));
					else
						nodeMissing = true;
			string note = null;
			if (offline.Status != TaskStatus.RanToCompletion)
				note = "Grids in areas nobody is near could not be listed. Try again.";
			else if (grids != null)
			{
				// A node's report wins: a partition that just woke can still have its offline row
				HashSet<long> seen = grids.Select(grid => grid.Id).ToHashSet();
				grids.AddRange(offline.Result.Grids.Where(grid => !seen.Contains(grid.Id)));
				if (offline.Result.Unknown > 0) note = "Some grids in areas nobody is near are not listed yet. Try again later.";
			}
			if (nodeMissing) note = "A server of the cluster did not answer, so its grids are not listed. Try again.";
			MyAPIGateway.Utilities.InvokeOnGameThread(() => context.Respond(grids == null
				? "The other servers of the cluster did not answer. Try again."
				: (Format(identity, caller, grids, note == null).TrimEnd() + "\n" + note).TrimEnd()));
		});
	}

	// A Fenced answer can be a fence that moved after the roster was read: a new broadcast reads the roster again
	private static async Task<PluginBroadcastResult> AskNodes(long identity)
	{
		byte[] payload = Encoding.UTF8.GetBytes(identity.ToString(CultureInfo.InvariantCulture));
		for (int attempt = 1; ; attempt++)
		{
			PluginBroadcastResult result = await PluginServices.Instance.BroadcastAsync(Plugin, Topic, payload, Guid.NewGuid(), false, Timeout, default).ConfigureAwait(false);
			if (attempt == 3 || result.Code != PluginResultCode.Fenced) return result;
		}
	}

	// Game thread, on every node: the player's grids this node owns (copies of other nodes' grids left out)
	private static Task<byte[]> OnList(PluginMessage message)
	{
		var lines = new List<Line>();
		if (long.TryParse(Encoding.UTF8.GetString(message.Payload ?? Array.Empty<byte>()), out long identity))
			foreach (MyCubeGrid grid in MyEntities.GetEntities().OfType<MyCubeGrid>())
			{
				if (grid.Projector != null || grid.MarkedForClose || grid.MarkedAsTrash || !grid.InScene || !grid.BigOwners.Contains(identity)) continue;
				PluginEntityPlacement placement = PluginServices.Instance.LocateEntity(grid.EntityId);
				// Unowned also covers a layout this node just made, a new grid's or a split's, until the registry answers its claim
				if (placement.Residence is PluginEntityResidence.Absent
					|| placement.Residence is PluginEntityResidence.Unowned && !ClusterRuntime.PartitionClaimPending(placement.Partition)) continue;
				Vector3D position = grid.PositionComp.GetPosition();
				// Going offline, frozen for its Final: its row can be there before the entities close, and this report would win over it
				lines.Add(new Line(grid.EntityId, grid.DisplayName, grid.GridSizeEnum.ToString(), grid.BlocksCount, position.X, position.Y, position.Z,
					ClusterRuntime.PartitionOfflining(placement.Partition)));
			}
		return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(lines));
	}

	private static async Task<(List<Line>, int)> OfflineGrids(long identity)
	{
		using HttpClient client = ClusterHttp.Client(Timeout);
		ClusterRuntime.AuthorizeRegistryClient(client);
		string root = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
		string text = await client.GetStringAsync(root + "/registry/partitions/offline/grids?owner=" + identity.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
		using JsonDocument document = JsonDocument.Parse(text);
		var grids = new List<Line>();
		foreach (JsonElement row in document.RootElement.GetProperty("grids").EnumerateArray())
		{
			JsonElement grid = row.GetProperty("grid");
			grids.Add(new Line(grid.GetProperty("id").GetInt64(), grid.GetProperty("n").GetString(), grid.GetProperty("l").GetBoolean() ? "Large" : "Small",
				grid.GetProperty("b").GetInt32(), grid.GetProperty("x").GetDouble(), grid.GetProperty("y").GetDouble(), grid.GetProperty("z").GetDouble(), true));
		}
		return (grids, document.RootElement.GetProperty("unknown").GetInt32());
	}

	private static string Format(long identity, string caller, List<Line> grids, bool complete)
	{
		UpdateMarkers(identity, grids, complete);
		if (grids.Count == 0) return "You own no grids.";
		bool positions = EssentialsOption("UtilityShowPosition");
		var text = new StringBuilder();
		text.AppendLine("Ships/stations owned by " + caller + ":");
		foreach (Line grid in grids.OrderBy(grid => grid.Name))
			text.AppendLine($"{grid.Name} - {grid.Size} - {grid.Blocks:#,##0} block(s) - Position "
				+ (positions ? new Vector3D(grid.X, grid.Y, grid.Z).ToString() : "Unknown") + (grid.Offline ? " - Offline" : ""));
		return text.ToString();
	}

	// As Essentials does it: a marker of an earlier list is stale once its grid has moved or is gone. A list that says it
	// is incomplete can't tell that.
	private static void UpdateMarkers(long identity, List<Line> grids, bool complete)
	{
		if (!EssentialsOption("MarkerShowPosition") || MyAPIGateway.Session?.GPS is not IMyGpsCollection markers) return;
		if (complete)
			foreach (IMyGps marker in markers.GetGpsList(identity).Where(IsGridMarker).ToList())
				if (!grids.Any(grid => grid.Name == marker.Name && new Vector3D(grid.X, grid.Y, grid.Z) == marker.Coords))
					markers.RemoveGps(identity, marker);
		foreach (Line grid in grids)
			markers.AddGps(identity, markers.Create(grid.Name, $"{grid.Name} - {grid.Size} - {grid.Blocks:#,##0} block(s)", new Vector3D(grid.X, grid.Y, grid.Z), true));
	}

	private static bool IsGridMarker(IMyGps gps) => gps.Description != null
		&& gps.Description.StartsWith(gps.Name + " - ", StringComparison.Ordinal)
		&& gps.Description.EndsWith(" block(s)", StringComparison.Ordinal);

	private static bool EssentialsOption(string name)
	{
		try
		{
			return string.Equals(XDocument.Load(Path.Combine(MyFileSystem.UserDataPath, "Essentials.cfg")).Root?.Element(name)?.Value, "true", StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
		{
			return false;
		}
	}
}

[CommandRoot("grids", "Grids", "Your grids")]
public sealed class GridListAlias : CommandModule
{
	[Command("list", "List your grids, on any server of the cluster and in areas nobody is near.")]
	[Permission(MyPromoteLevel.None)]
	public void List() => GridListCommand.List(Context);
}
