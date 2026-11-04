using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClusterPlugin;

internal static class OfflineRateCache
{
	private const string Subsystem = "offline-rates";
	private static long m_next;
	internal static void Install()
	{
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, Receive);
		GlobalStateChannel.RegisterCommandHandler(Subsystem + "-clear", (_, key) => ClearOnAuthority(key), journaled: true);
		GlobalStateChannel.RegisterCommandHandler(Subsystem + "-clear-epoch", (_, payload) =>
		{
			var request = JsonSerializer.Deserialize<ClearRequest>(payload) ?? throw new ArgumentException("Missing cache clear request");
			return ClearOnAuthority(request.Key, request.ExpectedEpoch);
		}, journaled: true);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, Restore);
		GlobalStateChannel.RegisterSnapshot(Subsystem, PersistedRecord, Restore);
	}
	internal static void Update()
	{
		if (!ClusterRuntime.IsClustered || Environment.TickCount64 < m_next) return;
		m_next = Environment.TickCount64 + 5000;
		string payload;
		payload = (string)OfflineCompensation.Call("CacheContributions");
		if (payload == "[]") return;
		_ = Task.Run(() =>
		{
			try { if (ClusterRuntime.IsWorldAuthority) Receive("local", payload); else GlobalStateChannel.SendRequest(Subsystem, payload); }
			catch (Exception e) { Console.WriteLine("[ClusterPlugin] offline rate cache publish deferred: " + SecretRedactor.Redact(e.Message)); }
		});
	}
	private static void Receive(string origin, string payload)
	{
		OfflineCompensation.Call("CacheReceive", payload);
		if (ClusterRuntime.IsWorldAuthority) GlobalStateChannel.Publish(Subsystem, PersistedRecord());
	}
	internal static string PersistedRecord() => (string)OfflineCompensation.Call("CacheSnapshot");
	internal static void Restore(string payload) => OfflineCompensation.Call("CacheRestore", payload);
	internal static string Clear(string key) => ClusterRuntime.IsWorldAuthority ? GlobalStateChannel.ExecuteLocalCommand(Subsystem + "-clear", key) : GlobalStateChannel.SendCommand(Subsystem + "-clear", key);
	private sealed record ClearRequest(string Key, int ExpectedEpoch);
	internal static string Clear(string key, int expectedEpoch, Guid operationId)
	{
		if (expectedEpoch < 0) throw new ArgumentException("expectedEpoch must be non-negative");
		string payload = JsonSerializer.Serialize(new ClearRequest(key, expectedEpoch));
		return ClusterRuntime.IsWorldAuthority ? GlobalStateChannel.ExecuteLocalCommand(Subsystem + "-clear-epoch", payload, operationId)
			: GlobalStateChannel.SendCommand(Subsystem + "-clear-epoch", payload, operationId);
	}
	private static string ClearOnAuthority(string key, int? expectedEpoch = null)
	{
		string result = (string)OfflineCompensation.Call("CacheClear", key, expectedEpoch);
		using var reply = JsonDocument.Parse(result);
		if (!reply.RootElement.GetProperty("cleared").GetBoolean()) return result;
		GlobalStateChannel.Publish(Subsystem, PersistedRecord());
		ClusterRuntime.RequestGlobalSave();
		return result;
	}

	internal static object Status() => OfflineCompensation.Call("CacheStatus");
}
