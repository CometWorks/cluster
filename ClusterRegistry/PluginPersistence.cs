using System.Buffers.Binary;
using System.Text.Json;
using PluginSdk.Clustering;

namespace ClusterRegistry;

internal sealed partial class GatewayRegistry
{
    // CGR3 contains one committed plugin mutation, not the whole Registry. CGR1/2 remain readable.
    private const uint MagicPluginMutation = 0x33524743;
    private sealed record PluginMutation(long StateSequence, Guid StoreId, string Key, string ReplayKey,
        PluginReplay Replay, string[] RemovedReplays);
    private bool _pluginBaseline;
    private DateTimeOffset _pluginWriteWindow;
    private int _pluginWrites;
    private readonly Dictionary<string, int> _pluginWritesByOwner = new(StringComparer.Ordinal);

    private bool AcceptPluginWrite(string plugin)
    {
        var now = _clock();
        if (now < _pluginWriteWindow || now - _pluginWriteWindow >= TimeSpan.FromSeconds(1))
        {
            _pluginWriteWindow = now;
            _pluginWrites = 0;
            _pluginWritesByOwner.Clear();
        }
        _pluginWritesByOwner.TryGetValue(plugin, out int writes);
        if (_pluginWrites >= 20 || writes >= 10) return false;
        _pluginWrites++;
        _pluginWritesByOwner[plugin] = writes + 1;
        return true;
    }

    private void EnsurePluginBaseline()
    {
        if (_pluginBaseline) return;
        // Persist the StoreId and registration context once before any delta can depend on them.
        Persist(); Flush();
        _pluginBaseline = true;
    }

    private void PersistPluginWrite(PluginRequest request, string[] priorReplays)
    {
        // T-0227: a plugin write is its own commit point. The PluginStore lives in the registry
        // store's state table, so the write is one store transaction, durable before the success
        // is acknowledged (the old CGR3 WAL delta record is only read now, when migrating).
        _ = request;
        _ = priorReplays;
        Persist();
        FlushState();
    }

    private void RecoverPluginWrite(byte[] bytes)
    {
        var mutation = JsonSerializer.Deserialize<PluginMutation>(bytes)
            ?? throw new InvalidDataException("Empty plugin WAL mutation.");
        if (mutation.StateSequence <= _state.StateSequence) return; // Covered by the published snapshot.
        if (mutation.StoreId != _state.PluginStore.StoreId || mutation.Replay?.Record?.Version?.StoreId != mutation.StoreId
            || mutation.Replay.Sequence <= _state.PluginStore.ReplaySequence || mutation.RemovedReplays is null
            || string.IsNullOrEmpty(mutation.Key) || string.IsNullOrEmpty(mutation.ReplayKey))
            throw new InvalidDataException("Plugin WAL identity or sequence mismatch.");
        foreach (string key in mutation.RemovedReplays) _state.PluginStore.Replays.Remove(key);
        _state.PluginStore.Records[mutation.Key] = mutation.Replay.Record;
        _state.PluginStore.Replays[mutation.ReplayKey] = mutation.Replay;
        _state.PluginStore.ReplaySequence = mutation.Replay.Sequence;
        _state.StateSequence = mutation.StateSequence;
    }
}
