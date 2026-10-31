using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClusterPlugin;

internal static class OfflineRateCache
{
	internal sealed record Entry(string Key, double Formula, double Rate, int Samples, double Spread, bool Scripted, int Epoch = 0);
	private const string Subsystem = "offline-rates";
	private static readonly object Sync = new();
	private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
	private static readonly Dictionary<string, Entry> Contributions = new(StringComparer.Ordinal);
	private static readonly Dictionary<string, int> Epochs = new(StringComparer.Ordinal);
	private sealed record Snapshot(Entry[] Entries, Dictionary<string, int> Epochs);
	private static long m_next;
	internal static void Install()
	{
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, Receive);
		GlobalStateChannel.RegisterCommandHandler(Subsystem + "-clear", (_, key) => ClearOnAuthority(key), journaled: true);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, Restore);
		GlobalStateChannel.RegisterSnapshot(Subsystem, PersistedRecord, Restore);
	}
	internal static bool TryGet(string key, out Entry entry)
	{
		lock (Sync) return Entries.TryGetValue(key, out entry) && !entry.Scripted;
	}
	internal static int Epoch(string key) { lock (Sync) return Epochs.GetValueOrDefault(key); }
	internal static void Measured(string key, double formula, double rate, int? epoch = null)
	{
		if (!double.IsFinite(rate) || rate <= 0 || !double.IsFinite(formula) || formula <= 0) return;
		lock (Sync)
		{
			if (epoch.HasValue && epoch.Value != Epochs.GetValueOrDefault(key)) return;
			double tolerance = double.Parse(ClusterConfig.Get("offline.cacheTolerance"), System.Globalization.CultureInfo.InvariantCulture);
			Entries.TryGetValue(key, out var previous);
			double spread = Math.Max(previous?.Spread ?? 0, previous == null ? 0 : Math.Abs(previous.Rate - rate) / Math.Max(rate, previous.Rate));
			var entry = new Entry(key, formula, rate, (previous?.Samples ?? 0) + 1, spread, spread > tolerance || Math.Abs(rate - formula) / formula > tolerance, Epochs.GetValueOrDefault(key));
			Contributions[key] = Entries[key] = entry;
		}
	}
	internal static void Update()
	{
		if (!ClusterRuntime.IsClustered || Environment.TickCount64 < m_next) return;
		m_next = Environment.TickCount64 + 5000;
		string payload;
		lock (Sync) { if (Contributions.Count == 0) return; payload = JsonSerializer.Serialize(Contributions.Values); }
		_ = Task.Run(() =>
		{
			try { if (ClusterRuntime.IsWorldAuthority) Receive("local", payload); else GlobalStateChannel.SendRequest(Subsystem, payload); }
			catch (Exception e) { Console.WriteLine("[ClusterPlugin] offline rate cache publish deferred: " + SecretRedactor.Redact(e.Message)); }
		});
	}
	private static void Receive(string origin, string payload)
	{
		Merge(JsonSerializer.Deserialize<Entry[]>(payload) ?? []);
		if (ClusterRuntime.IsWorldAuthority) GlobalStateChannel.Publish(Subsystem, PersistedRecord());
	}
	internal static string PersistedRecord() { lock (Sync) return JsonSerializer.Serialize(new Snapshot(Entries.Values.OrderBy(e => e.Key).ToArray(), new(Epochs))); }
	internal static void Restore(string payload)
	{
		if (string.IsNullOrEmpty(payload)) return;
		// The former array record remains readable; current snapshots replace the authoritative set.
		if (payload.TrimStart().StartsWith("[", StringComparison.Ordinal)) { Merge(JsonSerializer.Deserialize<Entry[]>(payload) ?? []); return; }
		var snapshot = JsonSerializer.Deserialize<Snapshot>(payload) ?? throw new InvalidOperationException("Missing offline rate cache snapshot");
		lock (Sync)
		{
			Entries.Clear(); Epochs.Clear();
			foreach (var epoch in snapshot.Epochs ?? []) Epochs[epoch.Key] = epoch.Value;
			Merge(snapshot.Entries ?? []);
			foreach (var old in Contributions.Where(c => c.Value.Epoch != Epochs.GetValueOrDefault(c.Key)).Select(c => c.Key).ToArray()) Contributions.Remove(old);
		}
	}
	private static void Merge(Entry[] entries)
	{
		lock (Sync)
			foreach (var entry in entries)
			{
				if (string.IsNullOrEmpty(entry.Key) || entry.Key.Length > 4096 || !double.IsFinite(entry.Rate) || entry.Rate <= 0 || entry.Samples <= 0
					|| entry.Epoch != Epochs.GetValueOrDefault(entry.Key)) continue;
				if (!Entries.TryGetValue(entry.Key, out var old) || entry.Samples > old.Samples || entry.Scripted && !old.Scripted) Entries[entry.Key] = entry;
				if (Contributions.TryGetValue(entry.Key, out var local) && entry.Samples >= local.Samples) Contributions.Remove(entry.Key);
			}
	}
	internal static string Clear(string key) => ClusterRuntime.IsWorldAuthority ? GlobalStateChannel.ExecuteLocalCommand(Subsystem + "-clear", key) : GlobalStateChannel.SendCommand(Subsystem + "-clear", key);
	private static string ClearOnAuthority(string key)
	{
		if (string.IsNullOrEmpty(key) || key.Length > 4096) throw new ArgumentException("A cache key is required");
		lock (Sync)
		{
			Epochs[key] = checked(Epochs.GetValueOrDefault(key) + 1);
			Entries.Remove(key); Contributions.Remove(key);
		}
		GlobalStateChannel.Publish(Subsystem, PersistedRecord());
		ClusterRuntime.RequestGlobalSave();
		return "{\"cleared\":true}";
	}

	internal static object Status() { lock (Sync) return new { count = Entries.Count, scripted = Entries.Values.Count(e => e.Scripted), entries = Entries.Values.ToArray() }; }
}
