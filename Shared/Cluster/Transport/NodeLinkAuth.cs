using System;
using System.Security.Cryptography;
using System.Text;

namespace ClusterPlugin;

/// <summary>
/// Join-token authentication for the node⇄Gateway Direct Transport link (Security.md §4.1–§4.3).
/// The registry-issued token is the cluster membership credential; the previous token stays valid
/// through a rotation window. Fail-closed: a node with no token configured accepts no node link.
/// </summary>
internal static class NodeLinkAuth
{
	private static byte[][] m_accepted;

	internal static byte[][] Accepted => m_accepted ?? (m_accepted = Load());

	private static byte[][] Load()
	{
		string current = Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN_CURRENT")
			?? Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN");
		string previous = Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN_PREVIOUS");
		var hashes = new System.Collections.Generic.List<byte[]>();
		if (!string.IsNullOrWhiteSpace(current)) hashes.Add(SHA256.HashData(Encoding.UTF8.GetBytes(current)));
		if (!string.IsNullOrWhiteSpace(previous)) hashes.Add(SHA256.HashData(Encoding.UTF8.GetBytes(previous)));
		return hashes.ToArray();
	}

	public static bool Accepts(string token)
	{
		byte[] supplied = SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty));
		bool accepted = false;
		foreach (byte[] expected in Accepted)
			accepted |= CryptographicOperations.FixedTimeEquals(supplied, expected);
		return accepted;
	}
}
