using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClusterPlugin;
using ClusterProtocol;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using VRage.Network;
using VRage.Utils;

namespace Shared.Patches;

/// <summary>
/// SE-1197: F3 Kick and Ban act on the whole cluster, through the Gateway.
///
/// Vanilla's KickUser and BanUser RPCs (the F3 list and the admin menu raise them) act on the node the admin is
/// attached to: the kick list and the ban config are node-local, so a kicked or banned player came back through
/// another node, and a player on another node was not kicked at all. On a cluster node the RPC handler, which runs
/// only after vanilla's admin check, forwards the action to the Gateway's admin moderation routes instead, the same
/// ones Quasar's player admin uses: POST /admin/v1/clients/{id}/kick and POST /admin/v1/admission/bans. Those arm
/// the cluster-wide kick cooldown or ban and disconnect the player from whichever node holds them. The node presents
/// its join token and names the admin it acts for; the Gateway checks that admin against its own administrator list
/// (see the Gateway's NodeModeration). The admin is told the outcome in chat. Unkick and unban stay vanilla: the
/// cluster-wide books are cleared from Quasar.
/// </summary>
public static class GatewayModerationPatch
{
	private static readonly HttpClient Gateway = ClusterHttp.Client(TimeSpan.FromSeconds(10));

	private static long m_forwarded, m_failed;

	/// <summary>Kicks and bans forwarded since the start, and how many of those the Gateway did not carry out.</summary>
	public static long Forwarded => Interlocked.Read(ref m_forwarded);

	public static long Failed => Interlocked.Read(ref m_failed);

	[HarmonyPatch(typeof(MyMultiplayerBase), nameof(MyMultiplayerBase.KickUser))]
	public static class KickUser
	{
		[HarmonyPrefix]
		private static bool Prefix(ulong userId, bool kicked) => !kicked || !Forward(userId, "kick");
	}

	[HarmonyPatch(typeof(MyMultiplayerBase), nameof(MyMultiplayerBase.BanUser))]
	public static class BanUser
	{
		[HarmonyPrefix]
		private static bool Prefix(ulong userId, bool banned) => !banned || !Forward(userId, "ban");
	}

	/// <summary>True when the action went to the Gateway and vanilla must not run.</summary>
	private static bool Forward(ulong userId, string action)
	{
		string gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
		MyEventContext context = MyEventContext.Current;
		if (!ClusterRuntime.IsClustered || string.IsNullOrEmpty(gateway) || !context.IsValid || context.IsLocallyInvoked)
			return false;
		ulong admin = context.Sender.Value;
		string name = OnlinePlayersService.Cluster.FirstOrDefault(entry => entry.SteamId == userId)?.Name;
		if (string.IsNullOrEmpty(name)) name = userId.ToString();
		Interlocked.Increment(ref m_forwarded);
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] " + action + " of player=" + userId + " by=" + admin
			+ " forwarded to the Gateway (SE-1197)");
		// Off the game thread: the round trip must never sit inside the update loop.
		new Thread(() => Send(gateway, userId, name, admin, action)) { IsBackground = true, Name = "ClusterGatewayModeration" }.Start();
		return true;
	}

	private static void Send(string gateway, ulong userId, string name, ulong admin, string action)
	{
		string error;
		try
		{
			using var request = action == "ban"
				? new HttpRequestMessage(HttpMethod.Post, gateway + "/admin/v1/admission/bans")
				{
					Content = new StringContent("{\"userId\":" + userId + "}", Encoding.UTF8, "application/json")
				}
				: new HttpRequestMessage(HttpMethod.Post, gateway + "/admin/v1/clients/" + userId + "/kick")
				{
					Content = new StringContent("{}", Encoding.UTF8, "application/json")
				};
			string token = ClusterRuntime.RegistryToken;
			if (!string.IsNullOrWhiteSpace(token))
				request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
			request.Headers.TryAddWithoutValidation(ClusterNet.ModeratorHeader, admin.ToString());
			request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("N"));
			using HttpResponseMessage response = Gateway.SendAsync(request).GetAwaiter().GetResult();
			error = Outcome((int)response.StatusCode, response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
		}
		catch (Exception e)
		{
			error = SecretRedactor.Redact(e.Message);
		}
		if (error != null) Interlocked.Increment(ref m_failed);
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] " + action + " of player=" + userId + " by=" + admin + " "
			+ (error == null ? "done by the Gateway" : "failed: " + error) + " (SE-1197)");
		string text = error == null
			? name + (action == "ban" ? " was banned from the cluster." : " was kicked from the cluster.")
			: "Could not " + action + " " + name + " on the cluster: " + error;
		MySandboxGame.Static?.Invoke(() => (MyMultiplayer.Static as MyDedicatedServerBase)?.SendChatMessageToPlayer(text, admin),
			"ClusterGatewayModeration");
	}

	/// <summary>Null when the Gateway carried the action out, else why not, from its admin envelope.</summary>
	internal static string Outcome(int status, string body)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
			JsonElement root = document.RootElement;
			if (root.TryGetProperty("data", out JsonElement data) && data.TryGetProperty("state", out JsonElement state))
			{
				if (state.GetString() == "Succeeded") return null;
				root = data;
			}
			if (root.TryGetProperty("error", out JsonElement error) && error.TryGetProperty("message", out JsonElement message))
				return message.GetString();
		}
		catch (JsonException) { }
		return "the Gateway answered " + status;
	}
}
