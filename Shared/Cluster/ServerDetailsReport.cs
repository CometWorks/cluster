using System;
using System.Threading;
using Sandbox;
using Sandbox.Game;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// SE-1076: the server details a game client shows for the cluster (Settings and Mods tabs) come from the "sc" A2S
/// rules, which a vanilla DS sets in MyCachedServerItem.SendSettingsToSteam from its session. The cluster's Steam
/// GameServer is the Gateway's, which has no session, so it asks a node for the same blob in the heartbeat reply
/// (ServerDataWanted) and the node answers with it in its next heartbeat. Computed once per process, on the game
/// thread, as vanilla does.
/// </summary>
internal static class ServerDetailsReport
{
	private static volatile string s_data;
	private static volatile bool s_wanted;
	private static int s_computing;

	/// <summary>The heartbeat's ServerData: the base64 blob while the registry asks for it, else null.</summary>
	public static string ForHeartbeat => s_wanted ? s_data : null;

	/// <summary>The heartbeat reply's ServerDataWanted.</summary>
	public static void Wanted(bool wanted)
	{
		s_wanted = wanted;
		if (wanted && s_data == null && MySandboxGame.Static != null && Interlocked.Exchange(ref s_computing, 1) == 0)
			MySandboxGame.Static.Invoke(Compute, "SE-1076 server details");
	}

	private static void Compute()
	{
		try
		{
			byte[] data = MyCachedServerItem.GetServerData();
			s_data = Convert.ToBase64String(data);
			MyLog.Default?.WriteLineAndConsole($"[ClusterPlugin] server details for the Gateway: {data.Length} bytes");
		}
		catch (Exception exception)
		{
			// No session yet, or a vanilla change: the next heartbeat reply asks again.
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] server details failed: " + exception.Message);
		}
		finally { Volatile.Write(ref s_computing, 0); }
	}
}
