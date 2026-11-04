using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ClusterPlugin;

internal sealed partial class PartitionPersistence
{
	/// <summary>Operator dry-run uses the exact offline Final, never newest-by-directory and never a
	/// recovery claim. This node/host must hold it; an uploaded copy is supported by the same API.</summary>
	internal PartitionSaveFile.Parsed ReadOfflineFinal(ulong partition, string expectedSave = null)
	{
		using HttpClient client = ClusterHttp.Client(TimeSpan.FromSeconds(15));
		ClusterRuntime.AuthorizeRegistryClient(client);
		using JsonDocument document = Get(client, "/registry/partitions/offline?partition=" + partition);
		var id = document.RootElement.GetProperty("Save");
		uint generation = checked((uint)id.GetProperty("Generation").GetInt64());
		uint sequence = checked((uint)id.GetProperty("Sequence").GetInt64());
		if (expectedSave != null && expectedSave != partition + ":" + generation + ":" + sequence)
			throw new InvalidOperationException("The offline Final changed during the request");
		string path = SavePath(partition, generation, sequence);
		if (!File.Exists(path)) throw new FileNotFoundException("The exact offline Final is not on this host; run on a holder or upload that Final", path);
		var parsed = PartitionSaveFile.ReadFile(path);
		PartitionSaveFile.ValidateEnvelope(parsed, partition, generation, sequence);
		if (parsed.Kind != 2) throw new InvalidDataException("Offline profile source is not a Final");
		return parsed;
	}
}
