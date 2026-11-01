using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

namespace ClusterPlugin;

/// <summary>Mod compatibility class (T-0242, #260). A: no scripts. B: entity-local scripts. C: global logic in
/// session components that only touches replicated global state (runs on the WA only). D: global decision plus
/// per-grid action or cluster-wide client messaging; correct only once adapted to the cluster mod channel.</summary>
public enum ModCompatClass { A, B, C, D }

/// <summary>Where a mod's session components run. Everywhere and Adapted both load on every process; Adapted
/// says the mod uses the cluster mod channel itself. WaOnly registers them on the World Authority alone.</summary>
public enum ModPlacement { Everywhere, WaOnly, Adapted }

/// <summary>One API group a mod assembly uses: call-site count and the first call site (Type.Method).</summary>
public sealed class ModApiUse
{
	public int Count { get; set; }
	public string FirstSite { get; set; }
}

/// <summary>What the static scan of one compiled mod assembly found, and the default verdict it implies.</summary>
public sealed class ModScanResult
{
	public string AssemblyName { get; set; }
	/// <summary>SHA-256 over every method's IL in metadata order. The script compiler does not emit
	/// deterministic images (MVID, timestamps), but the IL of the same sources is the same, so an unchanged
	/// mod keeps its hash across starts and an updated one changes it.</summary>
	public string IlSha256 { get; set; }
	public SortedDictionary<string, ModApiUse> Groups { get; } = new(StringComparer.Ordinal);
	/// <summary>Storage tier (world / local / global) to "read", "write" or "read+write".</summary>
	public SortedDictionary<string, string> StorageTiers { get; } = new(StringComparer.Ordinal);
	/// <summary>An author declaration found as a literal string field named <see cref="ModCompatScanner.AuthorConstantName"/>.</summary>
	public string AuthorConstant { get; set; }
	public string AuthorConstantSite { get; set; }
	public int UnresolvedCalls { get; set; }
	public ModCompatClass Class { get; set; }
	public ModPlacement Placement { get; set; }
	public string Reason { get; set; }

	public int Count(string group) => Groups.TryGetValue(group, out ModApiUse use) ? use.Count : 0;
}

/// <summary>
/// T-0242 (#260) item 4: static scan of a compiled mod assembly for the API groups that decide how a mod behaves on
/// a cluster, and the default class and placement they imply. Reflection and IL only, no game types, so the
/// offline tool Tools/ModCompatScan compiles this same file; it matches ModAPI members by interface and member
/// name, which also covers the ingame interface variants.
/// </summary>
public static class ModCompatScanner
{
	public const string SessionComponents = "session-components";
	public const string GameLogicComponents = "game-logic-components";
	public const string SessionState = "session-state-persisted";
	public const string WorldScans = "entity-world-scans";
	public const string PhysicsQueries = "physics-queries";
	public const string ClientMessaging = "client-messaging";
	public const string SetVariable = "set-variable";
	public const string FileReads = "file-storage-reads";
	public const string FileWrites = "file-storage-writes";
	public const string DamageSystem = "damage-system";
	public const string VslNotifications = "vsl-notifications";
	public const string ModChannel = "mod-channel-handshake";

	public static readonly string[] AllGroups =
	{
		SessionComponents, GameLogicComponents, SessionState, WorldScans, PhysicsQueries, ClientMessaging,
		SetVariable, FileReads, FileWrites, DamageSystem, VslNotifications, ModChannel,
	};

	/// <summary>Author declaration inside the mod: <c>const string ClusterModCompat = "C wa-only";</c> on any type.</summary>
	public const string AuthorConstantName = "ClusterModCompat";

	// ClusterModChannelService.SendMessageId / ReceiveMessageId ("CLUSMODS" / "CLUSMODR").
	private const long ChannelSendId = 0x434C_5553_4D4F_4453;
	private const long ChannelReceiveId = 0x434C_5553_4D4F_4452;

	private static readonly HashSet<string> WorldScanMethods = new(StringComparer.Ordinal)
	{
		"GetEntities", "GetEntitiesInSphere", "GetEntitiesInAABB", "GetTopMostEntitiesInSphere",
		"GetTopMostEntitiesInBox", "GetElementsInBox", "GetIntersectionWithSphere", "GetIntersectionWithLine",
	};

	private static readonly HashSet<string> VslMarkers = new(StringComparer.Ordinal)
	{
		"Notification", "ChatMessage", "Questlog", "MissionScreen",
	};

	private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
		| BindingFlags.Instance | BindingFlags.Static;

	private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
		.GetFields(BindingFlags.Public | BindingFlags.Static)
		.Select(field => (OpCode)field.GetValue(null))
		.ToDictionary(code => code.Value);

	/// <summary>Scans the compiled assemblies of one mod (the script manager builds one per Data/Scripts
	/// subfolder); none means the mod has no compiled scripts (class A).</summary>
	public static ModScanResult Scan(IReadOnlyCollection<Assembly> assemblies)
	{
		var result = new ModScanResult();
		if (assemblies == null || assemblies.Count == 0)
			return Classify(result, hasScripts: false);
		result.AssemblyName = string.Join(",", assemblies.Select(assembly => assembly.GetName().Name).OrderBy(name => name, StringComparer.Ordinal));
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (Assembly assembly in assemblies.OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal))
			ScanAssembly(result, assembly, hash);
		result.IlSha256 = Convert.ToHexString(hash.GetHashAndReset());
		return Classify(result, hasScripts: true);
	}

	private static void ScanAssembly(ModScanResult result, Assembly assembly, IncrementalHash hash)
	{
		Type[] types;
		try
		{
			types = assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException partial)
		{
			types = partial.Types.Where(type => type != null).ToArray();
		}
		foreach (Type type in types.OrderBy(type => type.MetadataToken))
		{
			string typeName = type.FullName ?? type.Name;
			bool sessionComponent = HasAttribute(type, "MySessionComponentDescriptor");
			if (sessionComponent)
			{
				Add(result, SessionComponents, typeName);
				if (type.GetMethod("GetObjectBuilder", Declared, null, Type.EmptyTypes, null) != null)
					Add(result, SessionState, typeName + ".GetObjectBuilder");
			}
			if (HasAttribute(type, "MyEntityComponentDescriptor"))
				Add(result, GameLogicComponents, typeName);
			foreach (FieldInfo field in type.GetFields(Declared))
			{
				if (field.IsLiteral && field.Name == AuthorConstantName && field.GetRawConstantValue() is string declared
					&& result.AuthorConstant == null)
				{
					result.AuthorConstant = declared;
					result.AuthorConstantSite = typeName + "." + field.Name;
				}
			}
			IEnumerable<MethodBase> methods = type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared));
			foreach (MethodBase method in methods.OrderBy(method => method.MetadataToken))
			{
				byte[] il;
				try
				{
					il = method.GetMethodBody()?.GetILAsByteArray();
				}
				catch (Exception)
				{
					il = null;
				}
				if (il == null)
					continue;
				hash.AppendData(Encoding.UTF8.GetBytes(typeName + "::" + method.Name + "\0"));
				hash.AppendData(il);
				ScanBody(result, method, il, typeName + "." + method.Name);
			}
		}
	}

	private static void ScanBody(ModScanResult result, MethodBase method, byte[] il, string site)
	{
		Type[] typeArguments = method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null;
		Type[] methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
		int position = 0;
		while (position < il.Length)
		{
			short value = il[position++];
			if (value == 0xFE && position < il.Length)
				value = unchecked((short)(0xFE00 | il[position++]));
			if (!OpCodesByValue.TryGetValue(value, out OpCode code))
				return; // not IL we understand: stop rather than misread operands
			switch (code.OperandType)
			{
				case OperandType.InlineNone:
					break;
				case OperandType.ShortInlineBrTarget:
				case OperandType.ShortInlineI:
				case OperandType.ShortInlineVar:
					position += 1;
					break;
				case OperandType.InlineVar:
					position += 2;
					break;
				case OperandType.InlineI8:
					long constant = BitConverter.ToInt64(il, position);
					if (constant is ChannelSendId or ChannelReceiveId)
						Add(result, ModChannel, site);
					position += 8;
					break;
				case OperandType.InlineR:
					position += 8;
					break;
				case OperandType.InlineSwitch:
					position += 4 + 4 * BitConverter.ToInt32(il, position);
					break;
				case OperandType.InlineMethod:
					int token = BitConverter.ToInt32(il, position);
					position += 4;
					MethodBase target;
					try
					{
						target = method.Module.ResolveMethod(token, typeArguments, methodArguments);
					}
					catch (Exception)
					{
						result.UnresolvedCalls++;
						break;
					}
					Match(result, target, site);
					break;
				default:
					position += 4;
					break;
			}
		}
	}

	private static void Match(ModScanResult result, MethodBase target, string site)
	{
		string owner = target.DeclaringType?.Name;
		if (owner == null)
			return;
		int arity = owner.IndexOf('`');
		if (arity >= 0)
			owner = owner.Substring(0, arity);
		string name = target.Name;
		switch (owner)
		{
			case "IMyEntities" when WorldScanMethods.Contains(name):
				Add(result, WorldScans, site);
				break;
			case "IMyPhysics" when name.StartsWith("Cast", StringComparison.Ordinal) || name.StartsWith("GetPenetrations", StringComparison.Ordinal):
				Add(result, PhysicsQueries, site);
				break;
			case "IMyMultiplayer" when name.StartsWith("SendMessageTo", StringComparison.Ordinal) && name != "SendMessageToServer":
				Add(result, ClientMessaging, site);
				break;
			case "IMyUtilities" when name is "SetVariable" or "RemoveVariable":
				Add(result, SetVariable, site);
				break;
			case "IMyUtilities" when name.EndsWith("Storage", StringComparison.Ordinal):
				bool write = name.StartsWith("Write", StringComparison.Ordinal) || name.StartsWith("Delete", StringComparison.Ordinal);
				bool read = name.StartsWith("Read", StringComparison.Ordinal) || name.StartsWith("FileExists", StringComparison.Ordinal);
				if (!write && !read)
					break;
				Add(result, write ? FileWrites : FileReads, site);
				string tier = name.EndsWith("WorldStorage", StringComparison.Ordinal) ? "world"
					: name.EndsWith("LocalStorage", StringComparison.Ordinal) ? "local" : "global";
				result.StorageTiers.TryGetValue(tier, out string seen);
				string use = write ? "write" : "read";
				result.StorageTiers[tier] = seen == null || seen == use ? use : "read+write";
				break;
			case "IMyDamageSystem":
			case "IMySession" when name == "get_DamageSystem":
				Add(result, DamageSystem, site);
				break;
			case "MyVisualScriptLogicProvider" when VslMarkers.Any(marker => name.Contains(marker, StringComparison.Ordinal)):
				Add(result, VslNotifications, site);
				break;
		}
	}

	/// <summary>
	/// The default verdict. A mod using the channel is adapted (D). Grid reach (world scans, physics queries) or
	/// client messaging (multiplayer sends, VSL notifications) narrows to the node, so a mod that needs either is D.
	/// A session-only mod that persists world state (SetVariable, world-storage writes, a session object builder)
	/// is C: only the WA's copy persists. Everything else is B. C is never chosen for a mod with game-logic components,
	/// because they may expect the session component that WA-only placement would skip on the nodes.
	/// </summary>
	private static ModScanResult Classify(ModScanResult result, bool hasScripts)
	{
		bool reach = result.Count(WorldScans) + result.Count(PhysicsQueries) > 0;
		bool clients = result.Count(ClientMessaging) + result.Count(VslNotifications) > 0;
		// world state: local and global storage are per machine (per node here) and mostly hold logs and client
		// settings, so only world-storage writes count, next to SetVariable and a session object builder
		bool worldWrites = result.StorageTiers.TryGetValue("world", out string worldUse) && worldUse != "read";
		bool global = worldWrites || result.Count(SetVariable) + result.Count(SessionState) > 0;
		(result.Class, result.Placement, result.Reason) =
			!hasScripts ? (ModCompatClass.A, ModPlacement.Everywhere, "no compiled scripts")
			: result.Count(ModChannel) > 0 ? (ModCompatClass.D, ModPlacement.Adapted, "uses the cluster mod channel")
			: reach || clients ? (ModCompatClass.D, ModPlacement.Everywhere, "needs adaptation: "
				+ (reach && clients ? "grid reach and client messaging narrow" : reach ? "grid reach narrows" : "client messaging narrows")
				+ " to this node")
			: result.Count(GameLogicComponents) > 0 ? (ModCompatClass.B, ModPlacement.Everywhere, "entity-local game logic")
			: result.Count(SessionComponents) > 0 && global
				? (ModCompatClass.C, ModPlacement.WaOnly, "session-only world state: only the WA copy persists")
			: (ModCompatClass.B, ModPlacement.Everywhere, "no cluster-relevant API use");
		return result;
	}

	private static void Add(ModScanResult result, string group, string site)
	{
		if (!result.Groups.TryGetValue(group, out ModApiUse use))
			result.Groups[group] = use = new ModApiUse { FirstSite = site };
		use.Count++;
	}

	private static bool HasAttribute(Type type, string name)
	{
		try
		{
			return type.GetCustomAttributesData().Any(data => data.AttributeType.Name == name + "Attribute" || data.AttributeType.Name == name);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Parses "&lt;class&gt; [placement]" or "&lt;placement&gt;" (the author and admin override syntax).
	/// A missing placement follows the class (C = wa-only, D = adapted, else everywhere); a missing class keeps
	/// <paramref name="fallbackClass"/>.</summary>
	public static bool TryParseOverride(string text, ModCompatClass fallbackClass, out ModCompatClass compatClass,
		out ModPlacement placement, out string error)
	{
		compatClass = fallbackClass;
		placement = ModPlacement.Everywhere;
		error = null;
		string[] words = (text ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
		bool classGiven = false, placementGiven = false;
		foreach (string word in words)
		{
			if (!classGiven && !placementGiven && word.Length == 1 && char.IsLetter(word[0]) && Enum.TryParse(word.ToUpperInvariant(), out ModCompatClass parsed)
				&& Enum.IsDefined(typeof(ModCompatClass), parsed))
			{
				compatClass = parsed;
				classGiven = true;
			}
			else if (!placementGiven && TryParsePlacement(word, out placement))
			{
				placementGiven = true;
			}
			else
			{
				error = $"cannot parse '{text}': expected '<A|B|C|D> [everywhere|nodes|wa-only|adapted]'";
				return false;
			}
		}
		if (!classGiven && !placementGiven)
		{
			error = "empty override";
			return false;
		}
		if (!placementGiven)
			placement = compatClass switch { ModCompatClass.C => ModPlacement.WaOnly, ModCompatClass.D => ModPlacement.Adapted, _ => ModPlacement.Everywhere };
		return true;
	}

	/// <summary>The author manifest (first line that is neither empty nor a # comment), or null.</summary>
	public static string ReadAuthorManifest(IEnumerable<string> lines) =>
		lines.Select(text => text.Trim()).FirstOrDefault(text => text.Length > 0 && !text.StartsWith('#'));

	/// <summary>Parses the admin placement file: "&lt;mod id&gt; &lt;override&gt;" per line, # comments. A mod id may
	/// carry the folder suffix ".sbm". Returns mod id to (override text, line number, line); bad lines go to
	/// <paramref name="rejected"/> with the reason.</summary>
	public static Dictionary<string, (string Value, int Number, string Line)> ParsePlacementFile(IEnumerable<string> lines,
		List<(int Number, string Line, string Reason)> rejected)
	{
		var entries = new Dictionary<string, (string, int, string)>(StringComparer.Ordinal);
		int number = 0;
		foreach (string raw in lines)
		{
			number++;
			string line = raw.Trim();
			if (line.Length == 0 || line.StartsWith('#'))
				continue;
			string[] parts = line.Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries);
			string id = parts[0].EndsWith(".sbm", StringComparison.OrdinalIgnoreCase) ? parts[0][..^4] : parts[0];
			if (parts.Length < 2)
				rejected.Add((number, line, "no verdict"));
			else if (entries.ContainsKey(id))
				rejected.Add((number, line, "duplicate mod id"));
			else
				entries[id] = (parts[1].Trim(), number, line);
		}
		return entries;
	}

	public static bool TryParsePlacement(string word, out ModPlacement placement)
	{
		switch (word.ToLowerInvariant())
		{
			case "everywhere":
			case "nodes":
				placement = ModPlacement.Everywhere;
				return true;
			case "wa-only":
			case "wa":
				placement = ModPlacement.WaOnly;
				return true;
			case "adapted":
				placement = ModPlacement.Adapted;
				return true;
			default:
				placement = ModPlacement.Everywhere;
				return false;
		}
	}

	public static string Name(ModPlacement placement) => placement switch
	{
		ModPlacement.WaOnly => "wa-only",
		ModPlacement.Adapted => "adapted",
		_ => "everywhere",
	};
}
