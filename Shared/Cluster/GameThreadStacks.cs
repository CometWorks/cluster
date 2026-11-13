using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// SE-883: a node whose game thread hangs while it owns nothing drains to Empty and ends through its exit watchdog. The
/// Registry never plans a kill, so Quasar.Host's hang capture does not run and the exit would be the only trace. The
/// watchdog logs every thread's stack first. The stacks come from CoreCLR's createdump: .NET has no API for another
/// thread's stack, and the runtime's diagnostics server runs the tool on its own native thread, permitted to attach
/// under Yama, with the game thread blocked. A crash report holds method names and no memory contents.
/// </summary>
internal static class GameThreadStacks
{
	private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
	private const int MaxLogChars = 64 * 1024;

	/// <summary>
	/// Never throws, and returns within <see cref="Limit"/> whatever the tool or the disk does: the process has to exit either way.
	/// <paramref name="source"/> starts the log line: "exit watchdog", or "long frame" (SE-1228).
	/// </summary>
	internal static void Log(string why, int gameThreadId, string source = "exit watchdog")
	{
		var capture = new Thread(() =>
		{
			string text;
			try { text = Capture(gameThreadId); }
			catch (Exception exception) { text = "stacks unavailable (" + exception.GetType().Name + "): " + exception.Message; }
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] " + source + ": " + why + "; " + text);
		})
		{ IsBackground = true, Name = "ClusterGameThreadStacks" };
		capture.Start();
		if (!capture.Join(Limit))
			MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] " + source + ": " + why + "; stacks unavailable: no answer in " + Limit.TotalSeconds + " s");
	}

	private static string Capture(int gameThreadId)
	{
		string path = Path.Combine(Path.GetTempPath(), "cluster-hung-node-" + Environment.ProcessId);
		string report = path + ".crashreport.json";
		try
		{
			const uint normalDump = 1, crashReportOnly = 8;
			RequestDump(path, normalDump, crashReportOnly);
			return "stacks:\n" + Render(File.ReadAllBytes(report), gameThreadId);
		}
		finally { File.Delete(report); }
	}

	/// <summary>The diagnostics IPC's GenerateCoreDump2. Answers once the tool has finished.</summary>
	internal static void RequestDump(string path, uint dumpType, uint flags)
	{
		const byte dumpCommandSet = 1, generateCoreDump2 = 2, serverCommandSet = 0xFF, serverOk = 0;
		string socketPath = Directory.EnumerateFiles(Path.GetTempPath(), "dotnet-diagnostic-" + Environment.ProcessId + "-*-socket").FirstOrDefault()
			?? throw new FileNotFoundException("no diagnostics socket");
		using var payload = new MemoryStream();
		using (var writer = new BinaryWriter(payload, Encoding.Unicode, leaveOpen: true))
		{
			writer.Write(path.Length + 1);
			writer.Write(Encoding.Unicode.GetBytes(path + '\0'));
			writer.Write(dumpType);
			writer.Write(flags);
		}
		using var message = new MemoryStream();
		using (var writer = new BinaryWriter(message, Encoding.ASCII, leaveOpen: true))
		{
			writer.Write(Encoding.ASCII.GetBytes("DOTNET_IPC_V1\0"));
			writer.Write((ushort)(20 + payload.Length));
			writer.Write(dumpCommandSet);
			writer.Write(generateCoreDump2);
			writer.Write((ushort)0);
			writer.Write(payload.ToArray());
		}
		using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		socket.Connect(new UnixDomainSocketEndPoint(socketPath));
		using var stream = new NetworkStream(socket);
		stream.Write(message.ToArray());
		var response = new byte[20];
		stream.ReadExactly(response);
		if (response[16] != serverCommandSet || response[17] != serverOk)
			throw new IOException("the runtime refused the dump, code 0x" + response[17].ToString("X2", CultureInfo.InvariantCulture));
	}

	/// <summary>The threads that run managed code, the game thread first. A run of frames in one native module is one line.</summary>
	internal static string Render(byte[] crashReport, int gameThreadId)
	{
		using JsonDocument document = JsonDocument.Parse(crashReport);
		var stacks = new List<(int Id, List<string> Frames)>();
		int nativeThreads = 0;
		foreach (JsonElement thread in document.RootElement.GetProperty("payload").GetProperty("threads").EnumerateArray())
		{
			var frames = new List<string>();
			bool managed = false;
			string module = null;
			int repeat = 0;
			void FlushNative()
			{
				if (module != null) frames.Add(repeat > 1 ? "[" + module + " x" + repeat + "]" : "[" + module + "]");
				module = null;
				repeat = 0;
			}
			if (thread.TryGetProperty("stack_frames", out JsonElement stack))
				foreach (JsonElement frame in stack.EnumerateArray())
				{
					if (frame.TryGetProperty("method_name", out JsonElement method) && !string.IsNullOrEmpty(method.GetString()))
					{
						FlushNative();
						frames.Add(method.GetString());
						managed = true;
						continue;
					}
					string current = frame.TryGetProperty("native_module", out JsonElement native) ? native.GetString() ?? "native" : "native";
					if (current != module) FlushNative();
					module = current;
					repeat++;
				}
			FlushNative();
			if (managed) stacks.Add((Convert.ToInt32(thread.GetProperty("native_thread_id").GetString(), 16), frames));
			else nativeThreads++;
		}
		var text = new StringBuilder();
		foreach ((int id, List<string> frames) in stacks.OrderByDescending(stack => stack.Id == gameThreadId))
		{
			text.Append("thread ").Append(id).Append(id == gameThreadId ? " (game thread)\n" : "\n");
			foreach (string frame in frames) text.Append("  ").Append(frame).Append('\n');
		}
		text.Append(nativeThreads).Append(" native threads not shown");
		return text.Length <= MaxLogChars ? text.ToString() : text.ToString(0, MaxLogChars);
	}
}
