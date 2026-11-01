using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace ClusterPlugin;

internal static class SaveRetryPolicy
{
	/// <summary>
	/// A Periodic save is retried once (the next capture supersedes it). T-0350: a HandoverTee the registry could not
	/// be reached for is given up after three attempts - its capture fails and the gateway aborts the transfer; retried
	/// without limit it kept the save queue full and the next capture waited on it on the game thread. A Final (drain,
	/// recovery) and a REJECTED tee keep retrying: nothing supersedes them.
	/// </summary>
	public static bool ShouldRetry(string kind, int attempts, bool unregistered = false) => kind switch
	{
		"Periodic" => attempts < 2,
		"HandoverTee" when unregistered => attempts < 3,
		_ => true,
	};

	/// <summary>
	/// T-0349: the registration did not reach the registry or got no usable answer (transport error, timeout, an empty
	/// answer, 5xx) - as opposed to the registry REJECTING it. The save file is written; only the catalogue entry is
	/// missing. That is not a save failure: reported as one, the registry Closes the node, so a 4 s registry outage
	/// drained node-79 on r116 and a network window would have drained every remote node.
	/// </summary>
	public static bool RegistryUnreachable(Exception exception) => exception switch
	{
		HttpRequestException request => request.StatusCode == null || (int)request.StatusCode >= 500,
		OperationCanceledException => true,
		JsonException => true,
		IOException => true,
		SocketException => true,
		_ => false,
	};

	/// <summary>
	/// T-0350: the registry gave NO answer (connection refused or reset, timeout) - the subset of
	/// <see cref="RegistryUnreachable"/> that any thread may set the registry hold on. A 5xx or an unreadable answer on
	/// one route is left to the heartbeat to judge: it would otherwise set a hold the next answered heartbeat clears, every
	/// second, for as long as that one route is broken.
	/// </summary>
	public static bool RegistryNoAnswer(Exception exception) => exception switch
	{
		RegistryUnreachableException wrapped => wrapped.InnerException != null && RegistryNoAnswer(wrapped.InnerException),
		HttpRequestException request => request.StatusCode == null,
		OperationCanceledException => true,
		IOException => true,
		SocketException => true,
		_ => false,
	};
}

/// <summary>T-0350: the small decisions the game thread takes while the registry is unreachable (ClusterRuntime.RegistryUnreachable).</summary>
internal static class RegistryHoldPolicy
{
	public enum Lookup { Cached, Fetch, Fail }

	/// <summary>
	/// A registry lookup cached on success only (node roster, node control endpoint). Fresh: the cache. Stale and the
	/// registry reachable: fetch, as before. Stale and the registry unreachable: the last answer whatever its age, and
	/// with none the caller's failure path at once - never an HTTP attempt on the game thread.
	/// </summary>
	public static Lookup CachedLookup(bool unreachable, bool cached, double now, double expires)
		=> cached && now < expires ? Lookup.Cached : !unreachable ? Lookup.Fetch : cached ? Lookup.Cached : Lookup.Fail;

	/// <summary>
	/// The heartbeat (1 Hz) was answered within the last 1.5 s: the registry is answering, whatever another request
	/// just ran into. In an outage this turns false one heartbeat period in - before any request's timeout ends.
	/// </summary>
	public static bool HeartbeatAnswering(long nowMilliseconds, long answeredMilliseconds) => nowMilliseconds - answeredMilliseconds < 1500;

	/// <summary>A line logged at most once per <paramref name="interval"/>: true (and the stamp taken) when it is due.</summary>
	public static bool LogDue(ref long last, long now, long interval)
	{
		if (last != 0 && now - last < interval) return false;
		last = now;
		return true;
	}
}

/// <summary>T-0349: a save whose file is written but whose registration could not reach the registry.</summary>
internal sealed class RegistryUnreachableException(string message, Exception inner) : Exception(message, inner)
{
}
