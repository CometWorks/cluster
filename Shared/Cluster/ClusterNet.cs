#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ClusterProtocol;

/// <summary>
/// T-0321 multi-host addressing and credential rules. Pure functions of their inputs, shared by the node
/// plugin and the registry: ClusterRegistry links this file, so its self-test exercises the node's decisions
/// (advertised endpoint, listener prefixes, per-route credential, rejection codes) without a live node.
/// </summary>
/// <remarks>
/// Single-host behaviour is the default of every function here: with no LAN address configured the node
/// registers, binds and authorizes exactly as before T-0321 (PLAN.md principle 3).
/// </remarks>
public static class ClusterNet
{
	/// <summary>The presented token is not (or no longer) accepted: a stale process from another start.</summary>
	public const string CredentialRejected = "credential_rejected";
	/// <summary>No token was presented. A configuration error, never a reason for the caller to exit.</summary>
	public const string CredentialRequired = "credential_required";
	/// <summary>The caller names a node id the registry retired.</summary>
	public const string NodeIdRetired = "node_id_retired";
	/// <summary>The caller names an epoch that is no longer the node's current incarnation.</summary>
	public const string StaleIncarnation = "stale_incarnation";

	/// <summary>Identity headers a node adds to its registry and peer requests. For logs, never a credential.</summary>
	public const string NodeHeader = "X-Cluster-Node", EpochHeader = "X-Cluster-Epoch", HostHeader = "X-Cluster-Host", PidHeader = "X-Cluster-Pid";

	/// <summary>
	/// The control endpoint a node registers, as <c>http://host:port</c>. <c>CLUSTER_NODE_CONTROL_ENDPOINT</c> (the
	/// address peers on other hosts can reach) wins; without it, the bind address, and loopback without a bind -
	/// the pre-T-0321 value.
	/// </summary>
	public static string AdvertisedControlEndpoint(string? advertised, string? bind, int port)
	{
		if (!string.IsNullOrWhiteSpace(advertised))
		{
			if (!Uri.TryCreate(advertised!.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttp)
				throw new ArgumentException("CLUSTER_NODE_CONTROL_ENDPOINT must be an absolute http:// URL, not '" + advertised + "'");
			return uri.GetLeftPart(UriPartial.Authority);
		}
		return new UriBuilder(Uri.UriSchemeHttp, string.IsNullOrWhiteSpace(bind) ? "127.0.0.1" : bind, port).Uri.GetLeftPart(UriPartial.Authority);
	}

	/// <summary>
	/// HttpListener prefixes for a control server bound to <paramref name="bind"/>: a loopback bind keeps its single
	/// prefix (unchanged); a LAN bind listens on the loopback prefix as well, so local tools keep working as before.
	/// </summary>
	public static string[] ControlPrefixes(IPAddress bind, int port, string path)
	{
		string own = "http://" + Literal(bind) + ":" + port.ToString(CultureInfo.InvariantCulture) + path;
		if (IPAddress.IsLoopback(bind)) return new[] { own };
		return new[] { "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + path, own };
	}

	public static string Literal(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();

	/// <summary>The credential a node control request needs.</summary>
	public enum ControlAccess { Open, Grant, JoinOrAdmin, Admin }

	/// <summary>
	/// What a node control request must present. Requests that arrived on the loopback prefix stay open, as every
	/// request was before T-0321 on a loopback-bound node. A request on the LAN prefix needs a credential - whichever
	/// host it came from, because a node always dials a peer's ADVERTISED address, so two nodes of one host
	/// authenticate each other too. Grant routes carry a one-time registry grant, validated separately.
	/// </summary>
	public static ControlAccess NodeControlAccess(bool arrivedOnLoopback, bool authConfigured, string path, string method)
	{
		if (IsGrantRoute(path)) return ControlAccess.Grant;
		if (!authConfigured || arrivedOnLoopback) return ControlAccess.Open;
		return IsPeerRoute(path, method) ? ControlAccess.JoinOrAdmin : ControlAccess.Admin;
	}

	public static bool IsGrantRoute(string path) => path.EndsWith("/prepare-import", StringComparison.OrdinalIgnoreCase)
		|| path.EndsWith("/import", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/save-file", StringComparison.OrdinalIgnoreCase);

	/// <summary>The node-to-node routes (CHANNELS.md N2-N11, and the gateway's C5 forward): the join token opens them.</summary>
	public static bool IsPeerRoute(string path, string method)
	{
		string route = Route(path);
		return route is "global-save-file" or "voxel-base-file" or "snapshot" or "igc-deliver" or "mod-deliver" or "mod-locate" or "partition-of"
			|| route == "partitions" && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
			|| route.EndsWith("-execute", StringComparison.Ordinal)
			|| route.StartsWith("voxel-journal", StringComparison.Ordinal);
	}

	/// <summary>The last path segment below <c>/cluster/</c>, lower case: <c>/cluster/voxel-journal/append</c> is <c>voxel-journal/append</c>.</summary>
	public static string Route(string path)
	{
		string trimmed = path.TrimEnd('/');
		int index = trimmed.IndexOf("/cluster/", StringComparison.OrdinalIgnoreCase);
		string route = index >= 0 ? trimmed.Substring(index + "/cluster/".Length) : trimmed.TrimStart('/');
		return route.ToLowerInvariant();
	}

	/// <summary>The token of an <c>Authorization: Bearer</c> header, or null.</summary>
	public static string? Bearer(string? header)
	{
		const string prefix = "Bearer ";
		return header != null && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && header.Length > prefix.Length
			? header.Substring(prefix.Length) : null;
	}

	/// <summary>Constant-time match of <paramref name="token"/> against every non-empty accepted token.</summary>
	public static bool TokenMatches(string? token, IEnumerable<string?> accepted)
	{
		if (token == null) return false;
		byte[] supplied = SHA256.HashData(Encoding.UTF8.GetBytes(token));
		bool match = false;
		foreach (string? candidate in accepted)
			if (!string.IsNullOrWhiteSpace(candidate))
				match |= CryptographicOperations.FixedTimeEquals(supplied, SHA256.HashData(Encoding.UTF8.GetBytes(candidate!)));
		return match;
	}

	/// <summary>A token's log identity: the first 12 hex digits of its SHA-256, never the token.</summary>
	public static string Fingerprint(string? token) => string.IsNullOrEmpty(token) ? "none"
		: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).Substring(0, 12).ToLowerInvariant();

	/// <summary>
	/// The explicit credential rejection in a 401/403 answer, or null. Only <see cref="CredentialRejected"/>,
	/// <see cref="NodeIdRetired"/> and <see cref="StaleIncarnation"/> count: a bare 401, a missing token, another
	/// status or an unreadable body never do, so an outage or a proxy error page can never be read as one.
	/// </summary>
	public static string? RejectionCode(int status, string? body)
	{
		if (status != 401 && status != 403 || string.IsNullOrWhiteSpace(body)) return null;
		try
		{
			using JsonDocument document = JsonDocument.Parse(body!);
			if (document.RootElement.ValueKind != JsonValueKind.Object
				|| !document.RootElement.TryGetProperty("code", out JsonElement code) || code.ValueKind != JsonValueKind.String)
				return null;
			string value = code.GetString()!;
			return value is CredentialRejected or NodeIdRetired or StaleIncarnation ? value : null;
		}
		catch (JsonException) { return null; }
	}

	/// <summary>What a node does with an answer (the T-0321 stale-node rule).</summary>
	public enum CredentialAction { None, ExitNode, ReportPeer }

	/// <summary>
	/// An explicit credential rejection from the REGISTRY ends this node (fence and exit without saving); the same from a
	/// PEER is only reported - the peer may be the stale one. Anything else (success, 5xx, 409, a bare 401, a missing
	/// token, a scope refusal) is no rejection. Timeouts and connection errors never produce an answer at all.
	/// </summary>
	public static CredentialAction OnAnswer(bool fromRegistry, int status, string? body) =>
		RejectionCode(status, body) == null ? CredentialAction.None : fromRegistry ? CredentialAction.ExitNode : CredentialAction.ReportPeer;

	/// <summary>The rejecting peer's self-identification in a credential rejection body (node, epoch), if present.</summary>
	public static (string? Node, long Epoch) RejectingPeer(string? body)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(body ?? "{}");
			JsonElement root = document.RootElement;
			string? node = root.TryGetProperty("node", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
			long epoch = root.TryGetProperty("epoch", out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long value) ? value : 0;
			return (node, epoch);
		}
		catch (JsonException) { return (null, 0); }
	}

	/// <summary>
	/// The CHANNELS.md id of an HTTP call. <paramref name="toRegistry"/>: the target is the registry (R*); otherwise a
	/// node control route, from a peer node (<paramref name="fromNode"/>, N*) or from the gateway (C*).
	/// </summary>
	public static string ChannelOf(bool toRegistry, bool fromNode, string path, string method)
	{
		string p = path.TrimEnd('/').ToLowerInvariant();
		if (toRegistry)
		{
			if (p == "/registry/nodes" || p.StartsWith("/registry/entity-id-ranges", StringComparison.Ordinal)
				|| p.StartsWith("/registry/network-id-blocks", StringComparison.Ordinal)) return "R1";
			if (p == "/registry/nodes/heartbeat") return "R2";
			if (p == "/registry/handover-config") return "R3";
			if (p == "/registry/grants/validate") return "R9";
			if (p.StartsWith("/registry/plugins", StringComparison.Ordinal)) return "R8";
			if (p.StartsWith("/registry/character-", StringComparison.Ordinal)) return "R6";
			if (p.StartsWith("/registry/saves", StringComparison.Ordinal) || p.StartsWith("/registry/replicas", StringComparison.Ordinal)
				|| p.StartsWith("/registry/deletions", StringComparison.Ordinal) || p.StartsWith("/registry/global-saves", StringComparison.Ordinal)
				|| p == "/registry/grants") return "R5";
			if (p.StartsWith("/registry/claims", StringComparison.Ordinal) || p.StartsWith("/registry/partition-ids", StringComparison.Ordinal)
				|| p.StartsWith("/registry/topology", StringComparison.Ordinal) || p.StartsWith("/registry/partitions/release", StringComparison.Ordinal)
				|| p.StartsWith("/registry/recoveries", StringComparison.Ordinal) || p == "/registry/nodes/empty"
				|| p == "/registry/partitions" && !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)) return "R4";
			return "R7";
		}
		string route = Route(p);
		if (fromNode)
		{
			if (route == "save-file") return "N1";
			if (route == "global-save-file") return "N2";
			if (route == "snapshot") return "N4";
			if (route.StartsWith("voxel-journal", StringComparison.Ordinal)) return "N5";
			if (route == "voxel-base-file") return "N6";
			if (route == "partitions") return "N7";
			if (route == "igc-deliver") return "N8";
			if (route is "mod-deliver" or "mod-locate") return "N9";
			if (route.EndsWith("-execute", StringComparison.Ordinal)) return "N10";
			if (route == "partition-of") return "N11";
		}
		if (route == "partitions") return "C1";
		if (route == "diag") return "C3";
		if (route == "respawn-execute") return "C5";
		if (route.StartsWith("trigger", StringComparison.Ordinal) || route is "config" or "shutdown-notice" or "return-to-menu"
			|| route.StartsWith("chat", StringComparison.Ordinal) || route.StartsWith("ship-storage", StringComparison.Ordinal)) return "C4";
		return "C2";
	}
}

/// <summary>Whether a peer is on this host (the same-host path) or across the LAN.</summary>
public enum TransportPath { Local, Lan, Fallback }

/// <summary>
/// T-0321 P6: per-channel path counters. Every inter-process HTTP call is counted under its CHANNELS.md id and
/// its path - local (the peer is this host: loopback or one of this host's own addresses) or lan - so the
/// acceptance can show which path each channel used. <c>fallback</c> counts calls a same-host fast path handed
/// to the LAN path; no channel has a fast path yet, so it stays 0 until one is adopted (PLAN §3).
/// </summary>
public static class TransportCounters
{
	private sealed class Cell
	{
		public long Count, Bytes;
		// log2 buckets of microseconds: bucket i holds [2^i, 2^(i+1)) us; 0 holds < 2 us.
		public readonly long[] Buckets = new long[32];
	}

	private sealed class Channel
	{
		public readonly Cell Local = new Cell(), Lan = new Cell(), InLocal = new Cell(), InLan = new Cell();
		public long Fallback;
	}

	private static readonly Dictionary<string, Channel> s_channels = new Dictionary<string, Channel>(StringComparer.Ordinal);
	private static IPAddress[] s_ownAddresses = Array.Empty<IPAddress>();
	private static long s_ownAddressesRead;

	/// <param name="incoming">A request this process SERVED (counted apart from the calls it made, whose path it chose).</param>
	public static void Count(string channel, bool incoming, TransportPath path, long bytes, TimeSpan elapsed)
	{
		lock (s_channels)
		{
			if (!s_channels.TryGetValue(channel, out Channel? cell)) s_channels[channel] = cell = new Channel();
			if (path == TransportPath.Fallback) { cell.Fallback++; return; }
			Cell target = incoming ? (path == TransportPath.Local ? cell.InLocal : cell.InLan) : path == TransportPath.Local ? cell.Local : cell.Lan;
			target.Count++;
			target.Bytes += Math.Max(0, bytes);
			long micros = Math.Max(0, (long)(elapsed.TotalMilliseconds * 1000));
			int bucket = 0;
			while (micros > 1 && bucket < target.Buckets.Length - 1) { micros >>= 1; bucket++; }
			target.Buckets[bucket]++;
		}
	}

	/// <summary>Local when <paramref name="host"/> is a loopback address or one of this machine's own addresses.</summary>
	public static TransportPath PathTo(string? host)
	{
		if (string.IsNullOrWhiteSpace(host)) return TransportPath.Lan;
		if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return TransportPath.Local;
		if (!IPAddress.TryParse(host!.Trim('[', ']'), out IPAddress? address)) return TransportPath.Lan;
		return IsOwnAddress(address) ? TransportPath.Local : TransportPath.Lan;
	}

	public static bool IsOwnAddress(IPAddress address)
	{
		if (IPAddress.IsLoopback(address)) return true;
		long now = Environment.TickCount64;
		if (now - Interlocked.Read(ref s_ownAddressesRead) > 60_000 || s_ownAddresses.Length == 0)
		{
			try
			{
				s_ownAddresses = NetworkInterface.GetAllNetworkInterfaces()
					.SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses.Select(unicast => unicast.Address)).ToArray();
			}
			catch (NetworkInformationException) { }
			Interlocked.Exchange(ref s_ownAddressesRead, now);
		}
		return s_ownAddresses.Contains(address);
	}

	/// <summary>The diag section: <c>{channels: {id: {local:{n,bytes,p50Ms,p99Ms}, lan:{...}, fallback, served:{local, lan}}}}</c>;
	/// local/lan are the calls this process made, served the requests it answered.</summary>
	public static object Snapshot()
	{
		lock (s_channels)
			return new
			{
				channels = s_channels.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToDictionary(entry => entry.Key, entry => (object)new
				{
					local = Describe(entry.Value.Local),
					lan = Describe(entry.Value.Lan),
					fallback = entry.Value.Fallback,
					served = new { local = Describe(entry.Value.InLocal), lan = Describe(entry.Value.InLan) },
				})
			};
	}

	internal static void ResetForTest()
	{
		lock (s_channels) s_channels.Clear();
	}

	private static object Describe(Cell cell) => new
	{
		n = cell.Count,
		bytes = cell.Bytes,
		p50Ms = Percentile(cell, 0.50),
		p99Ms = Percentile(cell, 0.99),
	};

	/// <summary>Upper bound of the log2 bucket holding the quantile, in ms (a factor-2 resolution).</summary>
	private static double? Percentile(Cell cell, double quantile)
	{
		if (cell.Count == 0) return null;
		long rank = (long)Math.Ceiling(cell.Count * quantile), seen = 0;
		for (int i = 0; i < cell.Buckets.Length; i++)
		{
			seen += cell.Buckets[i];
			if (seen >= rank) return Math.Pow(2, i + 1) / 1000.0;
		}
		return null;
	}
}
