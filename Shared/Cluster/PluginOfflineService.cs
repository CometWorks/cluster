using System;
using System.Collections.Generic;
using System.Linq;
using PluginSdk.Clustering;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// SE-903: plugins keeping partitions online (PluginSdk <c>RegisterOfflineBlocker</c>). Asked from
/// <see cref="PartitionPersistence.OfflineMemberBlocker"/> on the game thread, per member of every partition the
/// node might take offline. Nothing here outlives the process or follows a partition: each node asks the
/// blockers of its own plugins, which know their jobs.
/// </summary>
internal static class PluginOfflineService
{
	private static readonly object Sync = new object();
	private static KeyValuePair<string, Func<long, string>>[] s_blockers = Array.Empty<KeyValuePair<string, Func<long, string>>>();
	private static readonly HashSet<Func<long, string>> Logged = new HashSet<Func<long, string>>();

	public static IDisposable Register(string plugin, Func<long, string> blocker)
	{
		lock (Sync)
		{
			if (s_blockers.Any(x => x.Key == plugin)) throw new InvalidOperationException("Plugin " + plugin + " already has an offline blocker.");
			s_blockers = s_blockers.Append(new KeyValuePair<string, Func<long, string>>(plugin, blocker)).ToArray();
		}
		return new Registration(plugin, blocker);
	}

	private sealed class Registration : IDisposable
	{
		private readonly string plugin;
		private readonly Func<long, string> blocker;
		public Registration(string plugin, Func<long, string> blocker) { this.plugin = plugin; this.blocker = blocker; }
		public void Dispose()
		{
			lock (Sync)
			{
				s_blockers = s_blockers.Where(x => x.Key != plugin || x.Value != blocker).ToArray();
				Logged.Remove(blocker);
			}
		}
	}

	/// <summary>The first plugin's reason to keep this member online as "plugin/reason", or null. A plugin that throws
	/// keeps it online: staying costs memory, while going offline would stop whatever the plugin is doing there.</summary>
	public static string Blocker(long entityId)
	{
		foreach (KeyValuePair<string, Func<long, string>> entry in s_blockers)
		{
			try
			{
				string reason = entry.Value(entityId);
				if (!string.IsNullOrEmpty(reason)) return entry.Key + "/" + reason;
			}
			catch (Exception error)
			{
				bool first;
				lock (Sync) first = Logged.Add(entry.Value);
				if (first) MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] offline blocker of " + entry.Key + " threw, keeping partitions online: " + error);
				return entry.Key + "/error";
			}
		}
		return null;
	}
}
