using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ClusterProtocol;

namespace ClusterPlugin;

/// <summary>
/// T-0321: every HTTP client the node opens to the registry or to a peer node. One handler in front of all of
/// them, so three rules hold for every route without touching each call site:
/// <list type="bullet">
/// <item>the node's identity headers (node, epoch, host, pid) ride along, so the other side can log who asked;</item>
/// <item>each call is counted under its CHANNELS.md id and path, local or lan (<see cref="TransportCounters"/>);</item>
/// <item>an explicit credential rejection (<see cref="ClusterNet.RejectionCode"/>) is acted on: from the REGISTRY it
/// fences and exits this node; from a PEER it is reported to the registry and the caller carries on.</item>
/// </list>
/// Timeouts, connection errors and 5xx pass through untouched: they are never a rejection.
/// </summary>
internal static class ClusterHttp
{
	internal static HttpClient Client(TimeSpan timeout) => new HttpClient(new Handler { InnerHandler = new HttpClientHandler() }) { Timeout = timeout };

	private static string s_registryAuthority;

	/// <summary>The registry's scheme://host:port, from CLUSTER_GATEWAY_REGISTRY; null when not clustered.</summary>
	internal static string RegistryAuthority
	{
		get
		{
			string cached = Volatile.Read(ref s_registryAuthority);
			if (cached != null) return cached.Length == 0 ? null : cached;
			string value = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY");
			string authority = !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri uri)
				? uri.GetLeftPart(UriPartial.Authority) : string.Empty;
			Volatile.Write(ref s_registryAuthority, authority);
			return authority.Length == 0 ? null : authority;
		}
	}

	internal static bool IsRegistry(Uri uri) => uri != null && RegistryAuthority != null
		&& string.Equals(uri.GetLeftPart(UriPartial.Authority), RegistryAuthority, StringComparison.OrdinalIgnoreCase);

	internal static string NodeHost => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLUSTER_NODE_HOST"))
		? Environment.MachineName : Environment.GetEnvironmentVariable("CLUSTER_NODE_HOST");

	private static void Stamp(HttpRequestMessage request)
	{
		string node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID");
		if (string.IsNullOrWhiteSpace(node) || request.Headers.Contains(ClusterNet.NodeHeader)) return;
		request.Headers.TryAddWithoutValidation(ClusterNet.NodeHeader, node);
		request.Headers.TryAddWithoutValidation(ClusterNet.EpochHeader, Interlocked.Read(ref ClusterRuntime.RegistryNodeEpoch).ToString(CultureInfo.InvariantCulture));
		request.Headers.TryAddWithoutValidation(ClusterNet.HostHeader, NodeHost);
		request.Headers.TryAddWithoutValidation(ClusterNet.PidHeader, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
	}

	private static void Observe(HttpRequestMessage request, HttpResponseMessage response, long started, string body)
	{
		Uri uri = request.RequestUri;
		if (uri == null) return;
		bool registry = IsRegistry(uri);
		try
		{
			long bytes = (request.Content?.Headers.ContentLength ?? 0) + (response.Content?.Headers.ContentLength ?? 0);
			TransportCounters.Count(ClusterNet.ChannelOf(registry, fromNode: true, uri.AbsolutePath, request.Method.Method), incoming: false,
				TransportCounters.PathTo(uri.Host), bytes, Stopwatch.GetElapsedTime(started));
		}
		catch (Exception) { }
		if (body == null) return;
		ClusterNet.CredentialAction action = ClusterNet.OnAnswer(registry, (int)response.StatusCode, body);
		if (action == ClusterNet.CredentialAction.None) return;
		string code = ClusterNet.RejectionCode((int)response.StatusCode, body);
		string token = ClusterNet.Bearer(request.Headers.Authorization?.ToString());
		if (action == ClusterNet.CredentialAction.ExitNode) ClusterRuntime.RegistryCredentialRejected(uri, code, token);
		else ClusterRuntime.PeerCredentialRejected(uri, code, body, token);
	}

	private static bool MayBeRejection(HttpResponseMessage response) => (int)response.StatusCode is 401 or 403;

	private sealed class Handler : DelegatingHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Stamp(request);
			long started = Stopwatch.GetTimestamp();
			HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
			string body = null;
			if (MayBeRejection(response) && response.Content != null)
			{
				// Buffered, so the caller still reads the same body after us.
				await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
				body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
			}
			Observe(request, response, started, body);
			return response;
		}

		protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
			=> SendAsync(request, cancellationToken).GetAwaiter().GetResult();
	}
}
