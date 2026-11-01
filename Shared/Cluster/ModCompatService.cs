using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.ModAPI;

namespace ClusterPlugin;

/// <summary>
/// T-0242 (#260): per-mod compatibility verdict, placement and load-time record, on every node and the WA.
/// <para>
/// The mod list is world-global, so every process loads every mod. At session load each mod's compiled assembly
/// is scanned (<see cref="ModCompatScanner"/>) for a default class A-D and placement; an author declaration
/// (<see cref="AuthorManifestName"/> in the mod folder, or a <c>ClusterModCompat</c> string constant) overrides
/// it, and the admin's placement file (<see cref="PlacementFileName"/> in the mods folder, or the path in
/// CLUSTER_MOD_PLACEMENT; one "&lt;mod id&gt; &lt;class&gt; [placement]" per line) overrides both.
/// </para>
/// <para>
/// The only thing enforced is the placement: a WA-only mod's session components are not registered on regular
/// nodes (its game-logic components still run wherever their entity is). No mod is ever refused. Every mod gets one
/// record per process start: a readable <c>cluster_mod_compat</c> line, a <c>cluster_mod_compat_json</c> line for
/// harness assertions, and a WARN line per disagreement between the automatic, author and admin verdicts.
/// </para>
/// <para>
/// Writes through the mod file API are counted per mod and tier and logged at the first write of each pair; on a
/// regular node that write lands in the node's own storage, which only the WA's copy keeps cluster-wide. The
/// records and counters are served at GET /cluster/mod-compat.
/// </para>
/// </summary>
[HarmonyPatch]
internal static class ModCompatService
{
	public const string PlacementFileName = "cluster-mod-placement.txt";
	public const string AuthorManifestName = "cluster-mod-compat.txt";
	public const string PlacementEnvironmentVariable = "CLUSTER_MOD_PLACEMENT";

	private static readonly object Sync = new object();
	private static readonly Dictionary<string, Dictionary<string, object>> Records = new(StringComparer.Ordinal);
	private static readonly Dictionary<Assembly, string> ModOfAssembly = new();
	private static readonly Dictionary<(string Mod, string Tier), long> Writes = new();
	private static Dictionary<string, (string Value, string Source)> m_admin;
	private static string m_adminPath;

	internal static string Role => !ClusterRuntime.IsClustered ? "Standalone" : ClusterRuntime.IsWorldAuthority ? "WA" : "Regular";

	[HarmonyPrefix]
	[HarmonyPatch(typeof(MySession), "RegisterComponentsFromAssemblies")]
	private static void BeforeRegisterAll()
	{
		lock (Sync)
		{
			Records.Clear();
			ModOfAssembly.Clear();
			m_admin = null;
		}
	}

	/// <summary>Script mods: decide here, and skip the registration of a WA-only mod on a regular node.</summary>
	[HarmonyPrefix]
	[HarmonyPatch(typeof(MySession), nameof(MySession.RegisterComponentsFromAssembly), typeof(Assembly), typeof(bool), typeof(MyModContext))]
	private static bool BeforeRegister(MySession __instance, Assembly assembly, bool modAssembly, MyModContext context)
	{
		if (assembly == null || !modAssembly || context == null || context.IsBaseGame)
			return true;
		try
		{
			// a mod compiles to one assembly per Data/Scripts subfolder; vanilla registers the first one's components
			var assemblies = new List<Assembly> { assembly };
			if (__instance.ScriptManager?.ScriptsPerMod.TryGetValue(context, out HashSet<VRage.Utils.MyStringId> ids) == true)
				assemblies.AddRange(ids.Select(id => __instance.ScriptManager.Scripts.TryGetValue(id, out Assembly other) ? other : null)
					.Where(other => other != null && other != assembly));
			return !Evaluate(__instance, context, assemblies);
		}
		catch (Exception exception)
		{
			Log($"WARN cluster_mod_compat failed mod={context.ModId}: {exception.Message}; registering it as vanilla does");
			return true;
		}
	}

	/// <summary>Mods without compiled scripts never reach the per-assembly registration: record them here.</summary>
	[HarmonyPostfix]
	[HarmonyPatch(typeof(MySession), "RegisterComponentsFromAssemblies")]
	private static void AfterRegisterAll(MySession __instance)
	{
		try
		{
			foreach (MyObjectBuilder_Checkpoint.ModItem item in __instance.Mods ?? new List<MyObjectBuilder_Checkpoint.ModItem>())
			{
				var context = new MyModContext();
				context.Init(item);
				lock (Sync)
				{
					if (Records.ContainsKey(ModId(context)))
						continue;
				}
				Evaluate(__instance, context, new List<Assembly>());
			}
			lock (Sync)
			{
				Log($"cluster_mod_compat summary role={Role} mods={Records.Count} "
					+ string.Join(" ", Records.Values.GroupBy(record => (string)record["class"]).OrderBy(group => group.Key)
						.Select(group => group.Key + "=" + group.Count()))
					+ $" skippedHere={Records.Values.Count(record => (bool)record["skipped"])}");
			}
		}
		catch (Exception exception)
		{
			Log($"WARN cluster_mod_compat summary failed: {exception.Message}");
		}
	}

	/// <summary>Returns true when the mod's session components must not be registered on this process.</summary>
	private static bool Evaluate(MySession session, MyModContext context, List<Assembly> assemblies)
	{
		string modId = ModId(context);
		ModScanResult scan = ModCompatScanner.Scan(assemblies);
		Assembly assembly = assemblies.FirstOrDefault();
		var warnings = new List<string>();

		(string Value, string Source)? author = AuthorDeclaration(context.ModPath, scan, warnings);
		(string Value, string Source)? admin = AdminOverride(modId);
		var verdicts = new List<(string Name, ModCompatClass Class, ModPlacement Placement)> { ("automatic", scan.Class, scan.Placement) };
		object authorJson = Override("author", author, scan.Class, verdicts, warnings);
		object adminJson = Override("admin", admin, scan.Class, verdicts, warnings);
		(string decidedBy, ModCompatClass finalClass, ModPlacement placement) = verdicts[^1];
		for (int i = 0; i < verdicts.Count; i++)
		for (int j = i + 1; j < verdicts.Count; j++)
		{
			if (verdicts[i].Class != verdicts[j].Class || verdicts[i].Placement != verdicts[j].Placement)
				warnings.Add($"disagreement {verdicts[i].Name}={verdicts[i].Class}/{ModCompatScanner.Name(verdicts[i].Placement)}"
					+ $" {verdicts[j].Name}={verdicts[j].Class}/{ModCompatScanner.Name(verdicts[j].Placement)}");
		}
		if (placement == ModPlacement.WaOnly && scan.Count(ModCompatScanner.GameLogicComponents) > 0)
			warnings.Add("wa-only with game-logic components: they still run on the nodes, without this mod's session components");
		if (scan.UnresolvedCalls > 0)
			warnings.Add($"{scan.UnresolvedCalls} call site(s) could not be resolved; the scan may undercount");
		if (assembly == null && Directory.Exists(Path.Combine(context.ModPath ?? string.Empty, "Data", "Scripts")))
			warnings.Add("the mod has Data/Scripts but no compiled assembly (compile error?)");

		bool skipped = assembly != null && placement == ModPlacement.WaOnly && ClusterRuntime.IsClustered && !ClusterRuntime.IsWorldAuthority;
		string here = placement switch
		{
			ModPlacement.WaOnly => !ClusterRuntime.IsClustered ? "wa-only:standalone-loaded"
				: ClusterRuntime.IsWorldAuthority ? "wa-only:this-node-is-wa" : "wa-only:skipped-here",
			_ => ModCompatScanner.Name(placement),
		};

		// world and local storage are per assembly (the calling type's), global storage is shared by every mod
		string[] scopes = assemblies.Select(each => StripDll(each.ManifestModule.ScopeName)).ToArray();
		string worldRoot = session.CurrentPath ?? session.WorldSavePath;
		var storage = new SortedDictionary<string, object>(StringComparer.Ordinal);
		var storageParts = new List<string>();
		foreach ((string tier, string use) in scan.StorageTiers)
		{
			IEnumerable<string> paths = tier switch
			{
				"world" => worldRoot == null ? new string[] { null } : scopes.Select(scope => Path.Combine(worldRoot, "Storage", scope)),
				"local" => scopes.Select(scope => Path.Combine(MyFileSystem.UserDataPath, "Storage", scope)),
				_ => new[] { Path.Combine(MyFileSystem.UserDataPath, "Storage") },
			};
			string path = string.Join(";", paths.Select(each => each == null ? "?" : Path.GetFullPath(each)));
			storage[tier] = new { path, use };
			storageParts.Add($"{tier}:{use}@{path}");
		}

		var record = new Dictionary<string, object>
		{
			["event"] = "mod_compat",
			["node"] = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty,
			["role"] = Role,
			["mod"] = new
			{
				id = modId, name = context.ModName, service = context.ModServiceName, folder = context.ModId, path = context.ModPath,
			},
			["assembly"] = assembly == null ? null : new { name = scan.AssemblyName, ilSha256 = scan.IlSha256 },
			["class"] = finalClass.ToString(),
			["placement"] = ModCompatScanner.Name(placement),
			["here"] = here,
			["skipped"] = skipped,
			["decidedBy"] = decidedBy,
			["automatic"] = new
			{
				@class = scan.Class.ToString(),
				placement = ModCompatScanner.Name(scan.Placement),
				reason = scan.Reason,
				groups = ModCompatScanner.AllGroups.ToDictionary(group => group, group => scan.Groups.TryGetValue(group, out ModApiUse use)
					? new { count = use.Count, firstSite = use.FirstSite } : new { count = 0, firstSite = (string)null }),
				unresolvedCalls = scan.UnresolvedCalls,
			},
			["author"] = authorJson,
			["admin"] = adminJson,
			["storage"] = storage.Count == 0 ? "NONE" : storage,
			["warnings"] = warnings,
		};
		lock (Sync)
		{
			Records[modId] = record;
			foreach (Assembly each in assemblies)
				ModOfAssembly[each] = modId;
		}

		string groups = string.Join(",", scan.Groups.Select(pair => $"{pair.Key}:{pair.Value.Count}@{pair.Value.FirstSite}"));
		string storageText = storageParts.Count == 0 ? "NONE" : string.Join(",", storageParts);
		Log($"cluster_mod_compat mod={modId} name=\"{context.ModName}\" role={Role} class={finalClass}"
			+ $" placement={ModCompatScanner.Name(placement)} here={here} decidedBy={decidedBy}"
			+ $" auto={scan.Class}/{ModCompatScanner.Name(scan.Placement)} author={author?.Value ?? "NONE"} admin={admin?.Value ?? "NONE"}"
			+ $" il={scan.IlSha256 ?? "-"} groups={(groups.Length == 0 ? "NONE" : groups)} storage={storageText}");
		Log("cluster_mod_compat_json " + JsonSerializer.Serialize(record));
		foreach (string warning in warnings)
			Log($"WARN cluster_mod_compat mod={modId} {warning}");
		return skipped;
	}

	/// <summary>Adds an override's verdict to <paramref name="verdicts"/> and returns its record entry ("NONE" when absent).</summary>
	private static object Override(string name, (string Value, string Source)? found, ModCompatClass automatic,
		List<(string Name, ModCompatClass Class, ModPlacement Placement)> verdicts, List<string> warnings)
	{
		if (found == null)
			return "NONE";
		if (!ModCompatScanner.TryParseOverride(found.Value.Value, automatic, out ModCompatClass compatClass, out ModPlacement placement, out string error))
		{
			warnings.Add($"{name} override ignored at {found.Value.Source}: {error}");
			return new { value = found.Value.Value, source = found.Value.Source, error };
		}
		verdicts.Add((name, compatClass, placement));
		return new { value = found.Value.Value, source = found.Value.Source, @class = compatClass.ToString(), placement = ModCompatScanner.Name(placement) };
	}

	private static (string Value, string Source)? AuthorDeclaration(string modPath, ModScanResult scan, List<string> warnings)
	{
		(string Value, string Source)? manifest = null;
		string file = modPath == null ? null : Path.Combine(modPath, AuthorManifestName);
		if (file != null && File.Exists(file))
		{
			string line = ModCompatScanner.ReadAuthorManifest(File.ReadLines(file));
			if (line != null)
				manifest = (line, Path.GetFullPath(file));
		}
		(string Value, string Source)? constant = scan.AuthorConstant == null ? null : (scan.AuthorConstant.Trim(), "constant " + scan.AuthorConstantSite);
		if (manifest != null && constant != null && !string.Equals(manifest.Value.Value, constant.Value.Value, StringComparison.OrdinalIgnoreCase))
			warnings.Add($"author manifest '{manifest.Value.Value}' and {constant.Value.Source} '{constant.Value.Value}' differ; the manifest wins");
		return manifest ?? constant;
	}

	private static (string Value, string Source)? AdminOverride(string modId)
	{
		lock (Sync)
		{
			if (m_admin == null)
				LoadAdminFile();
			return m_admin.TryGetValue(modId, out (string Value, string Source) entry) ? entry : null;
		}
	}

	private static void LoadAdminFile()
	{
		m_admin = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
		string configured = Environment.GetEnvironmentVariable(PlacementEnvironmentVariable);
		m_adminPath = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Path.Combine(MyFileSystem.ModsPath, PlacementFileName) : configured);
		if (!File.Exists(m_adminPath))
		{
			Log($"cluster_mod_compat_config path={m_adminPath} absent: no admin overrides");
			return;
		}
		var rejected = new List<(int Number, string Line, string Reason)>();
		foreach ((string id, (string value, int number, string line)) in ModCompatScanner.ParsePlacementFile(File.ReadLines(m_adminPath), rejected))
			m_admin[id] = (value, $"{m_adminPath}:{number}: {line}");
		foreach ((int number, string line, string reason) in rejected)
			Log($"WARN cluster_mod_compat_config {m_adminPath}:{number} ignored ({reason}): {line}");
		Log($"cluster_mod_compat_config path={m_adminPath} entries={m_admin.Count}");
	}

	/// <summary>The Workshop / mod.io id ("1521905890"); a local mod keeps its folder name.</summary>
	private static string ModId(MyModContext context) =>
		context.ModItem.PublishedFileId != 0 ? context.ModItem.PublishedFileId.ToString() : context.ModId ?? context.ModName ?? "?";

	private static string StripDll(string name) =>
		name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

	[HarmonyPatch]
	private static class StorageWrites
	{
		private static IEnumerable<MethodBase> TargetMethods()
		{
			InterfaceMapping map = typeof(MyAPIUtilities).GetInterfaceMap(typeof(IMyUtilities));
			for (int i = 0; i < map.InterfaceMethods.Length; i++)
			{
				string name = map.InterfaceMethods[i].Name;
				if (name.EndsWith("Storage", StringComparison.Ordinal)
					&& (name.StartsWith("Write", StringComparison.Ordinal) || name.StartsWith("Delete", StringComparison.Ordinal)))
					yield return map.TargetMethods[i];
			}
		}

		private static void Prefix(MethodBase __originalMethod, object[] __args)
		{
			try
			{
				Count(__originalMethod.Name, __args.OfType<Type>().FirstOrDefault());
			}
			catch (Exception)
			{
				// counting must never break the mod's own write
			}
		}
	}

	private static void Count(string method, Type callingType)
	{
		string tier = method.EndsWith("WorldStorage", StringComparison.Ordinal) ? "world"
			: method.EndsWith("LocalStorage", StringComparison.Ordinal) ? "local" : "global";
		string mod;
		lock (Sync)
		{
			if (callingType == null || !ModOfAssembly.TryGetValue(callingType.Assembly, out mod))
				mod = new StackTrace(2, false).GetFrames()
					.Select(frame => frame.GetMethod()?.DeclaringType?.Assembly)
					.Where(assembly => assembly != null && ModOfAssembly.ContainsKey(assembly))
					.Select(assembly => ModOfAssembly[assembly]).FirstOrDefault() ?? "unknown";
			Writes.TryGetValue((mod, tier), out long count);
			Writes[(mod, tier)] = ++count;
			if (count != 1)
				return;
		}
		string method0 = method.Substring(method.LastIndexOf('.') + 1);
		Log(Role == "Regular"
			? $"WARN cluster_mod_storage_write mod={mod} tier={tier} op={method0} role=Regular: first write on this node; it stays in this node's storage, only the WA copy is kept cluster-wide"
			: $"cluster_mod_storage_write mod={mod} tier={tier} op={method0} role={Role}: first write");
	}

	/// <summary>GET /cluster/mod-compat: this process's records and write counters. Not on the game thread.</summary>
	public static object Status()
	{
		lock (Sync)
		{
			return new
			{
				node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty,
				role = Role,
				placementFile = m_adminPath,
				mods = Records.Values.ToList(),
				storageWrites = Writes.OrderBy(pair => pair.Key.Mod, StringComparer.Ordinal).ThenBy(pair => pair.Key.Tier, StringComparer.Ordinal)
					.Select(pair => new { mod = pair.Key.Mod, tier = pair.Key.Tier, count = pair.Value }).ToList(),
			};
		}
	}

	private static void Log(string line) => MySandboxGame.Log.WriteLineAndConsole("[ClusterPlugin] " + line);
}
