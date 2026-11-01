using System;
using System.Text.Json;

namespace ClusterPlugin;

public sealed record GlobalCommandResult(bool Success, string Payload, string Error);

public static class GlobalCommandCodec
{
	public static string Write(GlobalCommandResult result) => JsonSerializer.Serialize(result);

	public static GlobalCommandResult Read(string payload)
	{
		GlobalCommandResult result = JsonSerializer.Deserialize<GlobalCommandResult>(payload)
			?? throw new InvalidOperationException("Invalid global command response");
		if (result.Payload == null || result.Error == null || result.Payload.Length > 1024 * 1024 || result.Error.Length > 4096
			|| result.Success == (result.Error.Length != 0))
			throw new InvalidOperationException("Invalid global command response");
		return result;
	}
}
