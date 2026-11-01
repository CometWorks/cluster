using System;
using Sandbox;

namespace ClusterPlugin;

public static class MyServerWatcherPatch
{
	public static bool Prefix()
	{
		if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY"))) return true;
		if (MySandboxGame.ConfigDedicated != null) MySandboxGame.ConfigDedicated.AutoRestartEnabled = false;
		Console.WriteLine("[ClusterPlugin] vanilla server watcher disabled; cluster registry owns liveness");
		return false;
	}
}
