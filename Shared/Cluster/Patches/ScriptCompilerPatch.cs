using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using VRage.Scripting;

namespace ClusterPlugin;

internal static class ScriptCompilerPatch
{
	private static readonly ConcurrentDictionary<string, Entry> Cache = new ConcurrentDictionary<string, Entry>();
	private static readonly AsyncLocal<long?> StartedGeneration = new AsyncLocal<long?>();
	private static long m_generation;

	public static void Install()
	{
		MyScriptCompilerSeams.ClusterGetCompiled = Prefix;
		MyScriptCompilerSeams.ClusterStoreCompiled = StoreImage;
		MyScriptCompilerSeams.ClusterWhitelistChanged = Clear;
	}

	public static Assembly Prefix(MyScriptCompiler __instance, MyApiTarget target, IEnumerable<Script> scripts,
		List<Message> messages, bool enableDebugInformation, bool trackMemoryUsage)
	{
		long generation = Interlocked.Read(ref m_generation);
		StartedGeneration.Value = generation;
		if (target != MyApiTarget.Ingame || __instance.DiagnosticOutputPath != null
			|| !Cache.TryGetValue(Key(generation, scripts, enableDebugInformation, __instance.EnableDebugInformation, trackMemoryUsage), out Entry cached))
			return null;
		StartedGeneration.Value = null;
		messages.Clear();
		messages.AddRange(cached.Messages);
		return Assembly.Load(cached.Image);
	}

	public static void StoreImage(MyScriptCompiler __instance, MyApiTarget target, IEnumerable<Script> scripts,
		List<Message> messages, bool enableDebugInformation, bool trackMemoryUsage, byte[] image)
	{
		long? generation = StartedGeneration.Value;
		StartedGeneration.Value = null;
		if (target == MyApiTarget.Ingame && __instance.DiagnosticOutputPath == null && generation == Interlocked.Read(ref m_generation))
			Cache.TryAdd(Key(generation.Value, scripts, enableDebugInformation, __instance.EnableDebugInformation, trackMemoryUsage),
				new Entry(image, messages.ToArray()));
	}

	public static void Clear()
	{
		Interlocked.Increment(ref m_generation);
		Cache.Clear();
	}

	private static string Key(long generation, IEnumerable<Script> scripts, bool localDebug, bool globalDebug, bool trackMemory) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", scripts.Select(script =>
			script.Name.Length + ":" + script.Name + script.Code.Length + ":" + script.Code))
			+ "\0" + generation + "\0" + localDebug + "\0" + globalDebug + "\0" + trackMemory)));

	private sealed record Entry(byte[] Image, Message[] Messages);
}
