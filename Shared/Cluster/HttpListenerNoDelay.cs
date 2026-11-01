#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace ClusterProtocol;

/// <summary>
/// T-0324: TCP_NODELAY for a managed <see cref="HttpListener"/> (Linux/macOS; Windows uses http.sys).
/// </summary>
/// <remarks>
/// HttpResponseStream.WriteCore sends the headers plus the first 16384 bytes of the body in one socket write and
/// the rest of a Write in a second. With Nagle on, that second sub-MSS write waits for the ACK of the first, which
/// the client delays ~40 ms: on loopback (MSS ~65 KB) every Content-Length response of ~16-65 KB took 44 ms on a
/// kept-alive connection, and the small trailing writes of a chunked response did the same (T-0229). HttpListener
/// never sets TCP_NODELAY and has no option for it, so this sets it on the listener's own listening sockets, found
/// by reflection in HttpEndPointManager; Linux copies it to every socket accepted afterwards. Missing internals (a
/// runtime that renamed them, or http.sys) leave the listener as it was: logged, never thrown.
/// </remarks>
public static class HttpListenerNoDelay
{
	/// <summary>
	/// Sets TCP_NODELAY on every listening socket that serves a port of <paramref name="listener"/>'s prefixes. Call
	/// right after <see cref="HttpListener.Start"/>. Returns the number of sockets set; 0 with one log line otherwise.
	/// </summary>
	public static int Apply(HttpListener listener, Action<string> log)
	{
		try
		{
			var ports = new HashSet<int>();
			foreach (string prefix in listener.Prefixes)
				if (Uri.TryCreate(prefix.Replace("://*", "://localhost").Replace("://+", "://localhost"), UriKind.Absolute, out Uri? uri))
					ports.Add(uri.Port);
			Type? manager = typeof(HttpListener).Assembly.GetType("System.Net.HttpEndPointManager");
			FieldInfo? endPoints = manager?.GetField("s_ipEndPoints", BindingFlags.NonPublic | BindingFlags.Static);
			if (endPoints?.GetValue(null) is not IDictionary byAddress)
			{
				log("T-0324: HttpListener internals not found (" + (manager == null ? "no HttpEndPointManager" : "no s_ipEndPoints")
					+ "), TCP_NODELAY not set: 16-65 KB responses may wait ~40 ms on Nagle");
				return 0;
			}
			int set = 0;
			lock (((ICollection)byAddress).SyncRoot)
			{
				foreach (object? byPort in byAddress.Values)
				{
					if (byPort is not IDictionary listeners) continue;
					foreach (DictionaryEntry entry in listeners)
					{
						if (entry.Key is not int port || !ports.Contains(port) || entry.Value == null) continue;
						FieldInfo? field = entry.Value.GetType().GetField("_socket", BindingFlags.NonPublic | BindingFlags.Instance);
						if (field?.GetValue(entry.Value) is not Socket socket) continue;
						socket.NoDelay = true;
						set++;
					}
				}
			}
			log("T-0324: TCP_NODELAY on " + set + " HttpListener socket(s) for port(s) " + string.Join(",", ports));
			return set;
		}
		catch (Exception e)
		{
			log("T-0324: TCP_NODELAY not set on the HttpListener (" + e.GetType().Name + ": " + e.Message + ")");
			return 0;
		}
	}
}
