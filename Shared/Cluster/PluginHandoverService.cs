using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PluginSdk.Clustering;
using Sandbox.Game.Entities;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// T-0256: plugin grid handover hooks (PluginSdk <see cref="IPluginGridHandover"/>). The source asks each
/// registered plugin for a per-grid state at capture; the states ride the handover's network-state string
/// (opaque to the gateway) after a newline - the network-id entries never contain one - and the target hands
/// them to the same plugin at commit. Every hook runs on the game thread; a plugin's exception is logged and
/// costs only that plugin's call, never the handover.
/// </summary>
internal static class PluginHandoverService
{
	private const char Separator = '\n';
	/// <summary>All plugins' states for one capture; beyond it the remaining states are dropped (logged).</summary>
	private const int MaxCaptureBytes = 1024 * 1024;
	private static readonly object Sync = new object();
	private static readonly Dictionary<string, IPluginGridHandover> Handlers = new Dictionary<string, IPluginGridHandover>(StringComparer.Ordinal);

	public static IDisposable Register(string plugin, IPluginGridHandover handler)
	{
		lock (Sync)
		{
			if (Handlers.ContainsKey(plugin)) throw new InvalidOperationException("Plugin " + plugin + " already has a grid handover handler.");
			Handlers[plugin] = handler;
		}
		return new Registration(plugin, handler);
	}

	private sealed class Registration : IDisposable
	{
		private readonly string plugin;
		private readonly IPluginGridHandover handler;
		public Registration(string plugin, IPluginGridHandover handler) { this.plugin = plugin; this.handler = handler; }
		public void Dispose()
		{
			lock (Sync)
				if (Handlers.TryGetValue(plugin, out var current) && current == handler) Handlers.Remove(plugin);
		}
	}

	private static KeyValuePair<string, IPluginGridHandover>[] Snapshot()
	{
		lock (Sync) return Handlers.ToArray();
	}

	/// <summary>The grids among a handover's entities: the ones the hooks name.</summary>
	public static HashSet<long> Grids(IEnumerable<MyEntity> entities) =>
		entities.Where(entity => entity is MyCubeGrid).Select(entity => entity.EntityId).ToHashSet();

	/// <summary>
	/// T-0292: the cluster's own entry in the same per-entity map. The source's fast members and their measured
	/// speed (8 bytes, a double), so the target knows at commit that the arriving members are fast instead of
	/// re-measuring it over ~3 s, during which a mature arrival could be merged into a neighbour. It is keyed by
	/// entity id like the plugin states but may name a character; no plugin handler ever sees it.
	/// </summary>
	public const string FastKey = "cluster.fast";

	/// <summary>
	/// SE-1272: the source's grids whose rigid body Havok had put to sleep (one byte, no value). The commit's thaw
	/// enables every imported body, and Havok adds a body awake, so without this the target simulates a resting pile
	/// (Moon Station's 207 leftover shuttles, 122 of them touching) until it settles again. The target's
	/// ClusterControlServer.KeepAsleep puts them back to sleep. A handler of another plugin never sees this key.
	/// </summary>
	public const string AsleepKey = "cluster.asleep";

	/// <summary>Source, at capture: every plugin's state per grid as JSON {grid: {plugin: base64}}, or null.
	/// FAST adds the cluster's fast-member entries (<see cref="FastKey"/>), ASLEEP the sleeping grids (<see cref="AsleepKey"/>).</summary>
	public static string Leaving(HashSet<long> grids, IReadOnlyDictionary<long, double> fast = null, IEnumerable<long> asleep = null)
	{
		var handlers = Snapshot();
		var states = new Dictionary<string, Dictionary<string, byte[]>>();
		foreach ((long id, double speed) in fast ?? new Dictionary<long, double>())
			states[id.ToString()] = new Dictionary<string, byte[]> { [FastKey] = BitConverter.GetBytes(speed) };
		foreach (long id in asleep ?? Enumerable.Empty<long>())
		{
			if (!states.TryGetValue(id.ToString(), out var own)) states[id.ToString()] = own = new Dictionary<string, byte[]>();
			own[AsleepKey] = new byte[] { 1 };
		}
		if (handlers.Length == 0 || grids.Count == 0) return states.Count == 0 ? null : JsonSerializer.Serialize(states);
		long total = 0;
		foreach (long grid in grids.OrderBy(id => id))
			foreach (var (plugin, handler) in handlers)
			{
				byte[] state;
				try { state = handler.Leaving(grid); }
				catch (Exception error) { Log("Leaving", plugin, grid, error); continue; }
				if (state == null) continue;
				if (state.Length > PluginGridHandover.MaxStateBytes || total + state.Length > MaxCaptureBytes)
				{
					Console.WriteLine("[ClusterPlugin] plugin-handover state dropped plugin=" + plugin + " grid=" + grid
						+ " bytes=" + state.Length + " captureBytes=" + total);
					continue;
				}
				total += state.Length;
				if (!states.TryGetValue(grid.ToString(), out var perPlugin)) states[grid.ToString()] = perPlugin = new Dictionary<string, byte[]>();
				perPlugin[plugin] = state;
			}
		return states.Count == 0 ? null : JsonSerializer.Serialize(states);
	}

	/// <summary>Target: the fast members the source named (<see cref="FastKey"/>), id -> measured speed.</summary>
	public static Dictionary<long, double> FastArrivals(string pluginStates)
	{
		var fast = new Dictionary<long, double>();
		if (string.IsNullOrEmpty(pluginStates)) return fast;
		try
		{
			var states = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, byte[]>>>(pluginStates);
			foreach (var (id, perPlugin) in states ?? new Dictionary<string, Dictionary<string, byte[]>>())
				if (perPlugin != null && perPlugin.TryGetValue(FastKey, out byte[] bytes) && bytes?.Length == 8 && long.TryParse(id, out long entity))
					fast[entity] = BitConverter.ToDouble(bytes, 0);
		}
		catch (Exception error) { Console.WriteLine("[ClusterPlugin] fast arrivals unreadable: " + error.Message); }
		return fast;
	}

	/// <summary>Target: the grids the source had asleep (<see cref="AsleepKey"/>).</summary>
	public static HashSet<long> AsleepArrivals(string pluginStates)
	{
		var asleep = new HashSet<long>();
		if (string.IsNullOrEmpty(pluginStates)) return asleep;
		try
		{
			var states = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, byte[]>>>(pluginStates);
			foreach (var (id, perPlugin) in states ?? new Dictionary<string, Dictionary<string, byte[]>>())
				if (perPlugin != null && perPlugin.ContainsKey(AsleepKey) && long.TryParse(id, out long entity))
					asleep.Add(entity);
		}
		catch (Exception error) { Console.WriteLine("[ClusterPlugin] asleep arrivals unreadable: " + error.Message); }
		return asleep;
	}

	/// <summary>The network-state string with the plugin states appended (unchanged when there are none).</summary>
	public static string Append(string networkIds, string pluginStates) =>
		pluginStates == null ? networkIds : networkIds + Separator + pluginStates;

	/// <summary>Split a received network-state string into the network-id part and the plugin states (or null).</summary>
	public static string Split(string state, out string pluginStates)
	{
		int at = state?.IndexOf(Separator) ?? -1;
		pluginStates = at < 0 ? null : state.Substring(at + 1);
		return at < 0 ? state : state.Substring(0, at);
	}

	public static void Left(IEnumerable<long> grids) => Notify(grids, "Left", (handler, grid) => handler.Left(grid));

	public static void Stayed(IEnumerable<long> grids) => Notify(grids, "Stayed", (handler, grid) => handler.Stayed(grid));

	/// <summary>Target, at commit: <see cref="IPluginGridHandover.Arrived"/> for every arrived grid, with its state.</summary>
	public static void Arrived(IEnumerable<long> members, string pluginStates)
	{
		var handlers = Snapshot();
		if (handlers.Length == 0) return;
		Dictionary<string, Dictionary<string, byte[]>> states = null;
		if (!string.IsNullOrEmpty(pluginStates))
			try { states = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, byte[]>>>(pluginStates); }
			catch (Exception error) { Console.WriteLine("[ClusterPlugin] plugin-handover states unreadable: " + error.Message); }
		foreach (long grid in members.OrderBy(id => id))
		{
			if (!MyEntities.TryGetEntityById(grid, out MyEntity entity) || entity is not MyCubeGrid) continue;
			Dictionary<string, byte[]> perPlugin = null;
			states?.TryGetValue(grid.ToString(), out perPlugin);
			foreach (var (plugin, handler) in handlers)
			{
				byte[] state = null;
				perPlugin?.TryGetValue(plugin, out state);
				try { handler.Arrived(grid, state); }
				catch (Exception error) { Log("Arrived", plugin, grid, error); }
			}
		}
	}

	private static void Notify(IEnumerable<long> grids, string hook, Action<IPluginGridHandover, long> call)
	{
		if (grids == null) return;
		var handlers = Snapshot();
		foreach (long grid in grids.OrderBy(id => id))
			foreach (var (plugin, handler) in handlers)
				try { call(handler, grid); }
				catch (Exception error) { Log(hook, plugin, grid, error); }
	}

	private static void Log(string hook, string plugin, long grid, Exception error) =>
		Console.WriteLine("[ClusterPlugin] plugin-handover " + hook + " failed plugin=" + plugin + " grid=" + grid + ": "
			+ error.GetType().Name + ": " + error.Message);
}
