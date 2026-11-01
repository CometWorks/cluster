#nullable enable
using System;

namespace ClusterProtocol;

/// <summary>
/// T-0411: a request the managed <see cref="System.Net.HttpListener"/> has already answered itself. It answers a POST or PUT
/// that carries neither Content-Length nor Transfer-Encoding with 411 Length Required and closes the response - and STILL
/// hands the context to the application (2026-10-09 19:40:46: a bodyless POST /balance/cost-model got 411 while the gateway's
/// handler applied the override, and the handler's reply then threw ObjectDisposedException). The caller was told the request
/// failed, so nothing may run for it. DELETE, PATCH, OPTIONS and GET are not answered that way (measured on the same listener).
/// Shared by the gateway control listener and the node control server.
/// </summary>
public static class HttpListenerLengthRequired
{
	public static bool AlreadyAnswered(string method, string? contentLength, string? transferEncoding) =>
		(method.Equals("POST", StringComparison.OrdinalIgnoreCase) || method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
		&& contentLength == null && transferEncoding == null;

	public static string Describe(string method, string? path) =>
		$"verb={method} path={path ?? "?"} carries neither Content-Length nor Transfer-Encoding; the listener answered 411 Length"
		+ " Required, so nothing ran (send an empty body, e.g. curl -d '')";
}
