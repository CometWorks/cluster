using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.SessionComponents;
using VRage.Network;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// SE-1158: which DLCs and Steam inventory skins a player owns, as far as a node can tell.
///
/// A vanilla DS asks its Steam game server: <c>UserHasLicenseForApp</c> for DLCs, and the signed inventory blob the
/// client sends (<c>MySessionComponentGameInventory.RequestUpdateClientGameInventory</c>) for skins. A cluster node
/// has no Steam game server. Direct Transport replaces it, answers yes to every license and leaves the null
/// inventory service, which fails every blob. So every player owned every DLC block and no skin at all.
///
/// The Gateway's Steam frontend holds the player's auth session, so it asks Steam instead. The DLCs come with the
/// attach binding (<see cref="PlayerReady.MissingDlcs"/>) and travel to the other nodes in the online list
/// (<see cref="OnlinePlayersService.OnlinePlayerEntry.MissingDlcs"/>), which is what a node needs for the welders of
/// a player standing on another node. Skins are checked by the Gateway when the client sends its blob
/// (<see cref="CheckSkins"/>) and come with every later attach binding.
///
/// A player admitted without Steam (Direct Transport bots, EOS) has no answers, and the game's own check decides,
/// as before. A player online nowhere in the cluster owns no DLC, as on a vanilla DS, where Steam knows nothing about
/// someone who left.
/// </summary>
public static class PlayerEntitlements
{
	private sealed record Entry(uint[] MissingDlcs, HashSet<MyStringHash> Skins);

	/// <summary>Players attached to this node. They leave on detach; another node or the online list knows them then.</summary>
	private static readonly ConcurrentDictionary<ulong, Entry> Attached = new ConcurrentDictionary<ulong, Entry>();

	/// <summary>Skins the Gateway confirmed while the player was here, newer than the ones in the attach binding.</summary>
	private static readonly ConcurrentDictionary<ulong, HashSet<MyStringHash>> Confirmed = new ConcurrentDictionary<ulong, HashSet<MyStringHash>>();

	/// <summary>Players whose skins are being checked, so a client that resends its blob doesn't start a second check.</summary>
	private static readonly ConcurrentDictionary<ulong, byte> Checking = new ConcurrentDictionary<ulong, byte>();

	private static readonly HttpClient Gateway = ClusterHttp.Client(TimeSpan.FromSeconds(10));
	private static readonly string GatewayUrl = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');

	/// <summary>The Gateway route that deserializes a client's inventory blob with its Steam game server.</summary>
	public const string SkinCheckRoute = "/registry/steam/skins";

	public sealed record SkinCheckRequest(ulong UserId, string Data);
	public sealed record SkinCheckAnswer(string[] Skins);

	internal static void Install() => ClusterRuntime.AuthorizeRegistryClient(Gateway);

	internal static void Attach(PlayerReady ready)
	{
		Confirmed.TryRemove(ready.UserId, out _);
		Attached[ready.UserId] = new Entry(ready.MissingDlcs,
			ready.Skins == null ? null : new HashSet<MyStringHash>(ready.Skins.Select(MyStringHash.GetOrCompute)));
	}

	internal static void Detach(ulong clientId)
	{
		Attached.TryRemove(clientId, out _);
		Confirmed.TryRemove(clientId, out _);
	}

	/// <summary>For this node's online report: null when the Gateway didn't say.</summary>
	internal static uint[] MissingDlcs(ulong steamId) => Attached.TryGetValue(steamId, out Entry entry) ? entry.MissingDlcs : null;

	/// <summary>Whether the player owns the DLC; null leaves it to the game.</summary>
	public static bool? HasDlc(uint appId, ulong steamId)
	{
		if (!ClusterRuntime.IsClustered || steamId == 0) return null;
		bool attached = Attached.TryGetValue(steamId, out Entry local);
		return HasDlc(appId, steamId, attached, local?.MissingDlcs, OnlinePlayersService.Cluster);
	}

	/// <summary><see cref="HasDlc(uint, ulong)"/> from what this node knows: the player's attach binding if they are here, else the online list.</summary>
	internal static bool? HasDlc(uint appId, ulong steamId, bool attached, uint[] attachedMissing, OnlinePlayersService.OnlinePlayerEntry[] cluster)
	{
		if (attached) return attachedMissing == null ? null : !attachedMissing.Contains(appId);
		foreach (OnlinePlayersService.OnlinePlayerEntry player in cluster)
			if (player.SteamId == steamId)
				return player.MissingDlcs == null ? null : !player.MissingDlcs.Contains(appId);
		// Online nowhere. Until the first online list arrives nothing is known about anyone, so the game decides.
		return cluster.Length == 0 ? null : false;
	}

	/// <summary>Whether the player owns the skin; null leaves it to the game.</summary>
	public static bool? HasSkin(MyStringHash skin, ulong steamId)
	{
		if (!ClusterRuntime.IsClustered || steamId == 0) return null;
		if (Confirmed.TryGetValue(steamId, out HashSet<MyStringHash> confirmed)) return confirmed.Contains(skin);
		if (Attached.TryGetValue(steamId, out Entry local) && local.Skins != null) return local.Skins.Contains(skin);
		return null;
	}

	/// <summary>
	/// Sends the client's inventory blob to the Gateway, which deserializes it with Steam, and answers the client
	/// the way the game does once the skins are known. False when the cluster can't check it: the game goes on as
	/// before (and fails).
	/// </summary>
	public static bool CheckSkins(ulong steamId, byte[] checkData)
	{
		if (!ClusterRuntime.IsClustered || string.IsNullOrEmpty(GatewayUrl) || checkData == null || checkData.Length == 0) return false;
		if (!Checking.TryAdd(steamId, 0)) return true;
		_ = Task.Run(async () =>
		{
			try
			{
				string body = JsonSerializer.Serialize(new SkinCheckRequest(steamId, Convert.ToBase64String(checkData)));
				using var content = new StringContent(body, Encoding.UTF8, "application/json");
				using HttpResponseMessage response = await Gateway.PostAsync(GatewayUrl + SkinCheckRoute, content).ConfigureAwait(false);
				string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				if (!response.IsSuccessStatusCode)
				{
					Console.WriteLine($"[ClusterPlugin] skins_check client={steamId} status={(int)response.StatusCode} answer={text}");
					return;
				}
				string[] names = JsonSerializer.Deserialize<SkinCheckAnswer>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Skins ?? Array.Empty<string>();
				Console.WriteLine($"[ClusterPlugin] skins_check client={steamId} skins={names.Length}");
				MySandboxGame.Static.Invoke(() =>
				{
					if (!Attached.ContainsKey(steamId)) return;
					Confirmed[steamId] = new HashSet<MyStringHash>(names.Select(MyStringHash.GetOrCompute));
					MyMultiplayer.RaiseStaticEvent(_ => MySessionComponentGameInventory.UpdateClientGameInventoryResult, true,
						new EndpointId(steamId));
				}, "SE-1158 skins");
			}
			catch (Exception error)
			{
				Console.WriteLine($"[ClusterPlugin] skins_check client={steamId} error={error.Message}");
			}
			finally { Checking.TryRemove(steamId, out _); }
		});
		return true;
	}
}
