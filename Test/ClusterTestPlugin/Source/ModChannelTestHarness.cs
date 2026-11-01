using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using ClusterPlugin;

namespace ClusterTestPlugin;

/// <summary>
/// P2-MOD test seam. The lane cannot compile and load a workshop mod mid-run, so this harness sits in the exact
/// seat a mod occupies: it talks to the cluster mod channel only through
/// <c>MyAPIUtilities.Static.SendModMessage</c> / <c>RegisterMessageHandler</c> on the public channel ids -
/// nothing here bypasses the contract under test. The reference ClusterJanitor mod ships the same calls as
/// sandbox script source.
/// </summary>
public static class ModChannelTestHarness
{
	private static readonly object Sync = new object();
	private static readonly List<object> Received = new();

	/// <summary>
	/// Registers the mod-side handler. Called from ClusterSeams.ServicesInstalled (Plugin.Init is too
	/// early: MyAPIUtilities.Static does not exist before the session loads).
	/// </summary>
	internal static void Install()
	{
		MyAPIUtilities.Static.RegisterMessageHandler(ClusterModChannelService.ReceiveMessageId, HandleReceive);
	}

	/// <summary>Sends as a mod would. Game thread (control server Invoke).</summary>
	public static object Send(string kind, string channel, string payload, long correlation, string replyTo)
	{
		object[] message = kind switch
		{
			"broadcast" => new object[] { "broadcast", channel, payload },
			"request" => new object[] { "request", channel, payload, correlation },
			"respond" => new object[] { "respond", channel, payload, correlation, replyTo },
			_ => throw new ArgumentOutOfRangeException(nameof(kind), "unknown mod channel kind " + kind)
		};
		MyAPIUtilities.Static.SendModMessage(ClusterModChannelService.SendMessageId, message);
		return new { sent = true, kind, channel };
	}

	/// <summary>Drains everything the mod-side handler received, in arrival order.</summary>
	public static object Drain()
	{
		lock (Sync)
		{
			object[] drained = Received.ToArray();
			Received.Clear();
			return new { received = drained };
		}
	}

	private static void HandleReceive(object payload)
	{
		if (payload is not object[] parts || parts.Length < 3 || parts[0] is not string kind) return;
		object entry = kind switch
		{
			"broadcast" when parts.Length >= 4 => new
			{ kind, channel = parts[1], origin = parts[2], payload = parts[3] },
			"request" when parts.Length >= 5 => new
			{ kind, channel = parts[1], origin = parts[2], payload = parts[3], correlation = parts[4] },
			"response" when parts.Length >= 5 => new
			{ kind, channel = parts[1], origin = parts[2], payload = parts[3], correlation = parts[4] },
			"fault" when parts.Length >= 4 => new
			{ kind, channel = parts[1], origin = (object)null, payload = parts[2], correlation = parts[3] },
			_ => null
		};
		if (entry == null) return;
		lock (Sync) Received.Add(entry);
	}
}
