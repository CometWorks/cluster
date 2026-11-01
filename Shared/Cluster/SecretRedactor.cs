using System;
using System.Collections.Generic;
using System.Linq;

namespace ClusterPlugin;

internal static class SecretRedactor
{
	private static string[] m_values = Array.Empty<string>();

	public static void ConfigureFromEnvironment() => Configure(new[]
	{
		Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN"),
		Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN_CURRENT"),
		Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN_PREVIOUS"),
		Environment.GetEnvironmentVariable("CLUSTER_ADMIN_TOKEN")
	});

	internal static void Configure(IEnumerable<string> values) => m_values = values.Where(value => !string.IsNullOrEmpty(value))
		.SelectMany(value => new[] { value, Uri.EscapeDataString(value) }).Distinct(StringComparer.Ordinal)
		.OrderByDescending(value => value.Length).ToArray();

	public static string Redact(string value)
	{
		foreach (string secret in m_values) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
		return value;
	}
}
