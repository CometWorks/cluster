using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;
using ClusterPlugin;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ModCompatScan;

internal static class Program
{
	// MyScriptManager.UpdateCompatibility prepends these to every mod script before compiling it.
	private const string UsingHeader = "using VRage;\r\nusing VRage.Game.Components;\r\nusing VRage.ObjectBuilders;\r\nusing VRage.ModAPI;\r\n"
		+ "using VRage.Game.ModAPI;\r\nusing Sandbox.Common.ObjectBuilders;\r\nusing VRage.Game;\r\nusing Sandbox.ModAPI;\r\n"
		+ "using VRage.Game.ModAPI.Interfaces;\r\nusing SpaceEngineers.Game.ModAPI;\r\n#line 1\r\n";

	private const string AuthorManifestName = "cluster-mod-compat.txt";

	private static int Main(string[] args)
	{
		string dedicated64 = Environment.GetEnvironmentVariable("SPACE_ENGINEERS_BIN64")
			?? Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? ".", ".steam/steam/steamapps/common/SpaceEngineersDedicatedServer/DedicatedServer64");
		if (args.Length == 1 && args[0] == "--selftest")
			return SelfTest();
		bool json = false;
		string expectFile = null;
		var mods = new List<string>();
		for (int i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--dedicated64": dedicated64 = args[++i]; break;
				case "--json": json = true; break;
				case "--expect": expectFile = args[++i]; break;
				default: mods.Add(args[i]); break;
			}
		}
		if (mods.Count == 0)
		{
			Console.Error.WriteLine("usage: ModCompatScan [--dedicated64 DIR] [--json] [--expect FILE] MOD_FOLDER...");
			return 2;
		}
		Dictionary<string, string> expected = expectFile == null ? null : File.ReadLines(expectFile)
			.Select(line => line.Split('#')[0].Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
			.Where(parts => parts.Length >= 2).ToDictionary(parts => parts[0], parts => parts[1].ToUpperInvariant());

		AssemblyLoadContext.Default.Resolving += (context, name) =>
		{
			string candidate = Path.Combine(dedicated64, name.Name + ".dll");
			return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
		};
		List<MetadataReference> references = References(dedicated64);
		int failures = 0;
		foreach (string mod in mods)
		{
			string id = Path.GetFileName(Path.GetFullPath(mod).TrimEnd('/')).Replace(".sbm", "");
			List<Assembly> assemblies = Compile(id, mod, references, out List<string> errors);
			ModScanResult scan = ModCompatScanner.Scan(assemblies);
			string manifestPath = Path.Combine(mod, AuthorManifestName);
			string manifest = File.Exists(manifestPath) ? ModCompatScanner.ReadAuthorManifest(File.ReadLines(manifestPath)) : null;
			string verdict = "";
			if (expected != null && expected.TryGetValue(id, out string want))
			{
				bool pass = errors.Count == 0 && want == scan.Class.ToString();
				verdict = pass ? " expect=PASS" : $" expect=FAIL(wanted {want})";
				if (!pass)
					failures++;
			}
			if (json)
			{
				Console.WriteLine(JsonSerializer.Serialize(new
				{
					mod = id, assembly = scan.AssemblyName, ilSha256 = scan.IlSha256, @class = scan.Class.ToString(),
					placement = ModCompatScanner.Name(scan.Placement), reason = scan.Reason,
					groups = scan.Groups.ToDictionary(pair => pair.Key, pair => new { count = pair.Value.Count, firstSite = pair.Value.FirstSite }),
					storage = scan.StorageTiers, authorConstant = scan.AuthorConstant, authorManifest = manifest,
					unresolvedCalls = scan.UnresolvedCalls, compileErrors = errors,
				}));
				continue;
			}
			Console.WriteLine($"{id} class={scan.Class} placement={ModCompatScanner.Name(scan.Placement)} reason=\"{scan.Reason}\"{verdict}");
			foreach ((string group, ModApiUse use) in scan.Groups)
				Console.WriteLine($"    {group,-24} {use.Count,5}  first {use.FirstSite}");
			foreach ((string tier, string use) in scan.StorageTiers)
				Console.WriteLine($"    storage {tier}: {use}");
			if (scan.AuthorConstant != null || manifest != null)
				Console.WriteLine($"    author: manifest={manifest ?? "NONE"} constant={scan.AuthorConstant ?? "NONE"}");
			if (scan.UnresolvedCalls > 0)
				Console.WriteLine($"    unresolved call sites: {scan.UnresolvedCalls}");
			foreach (string error in errors.Take(5))
				Console.WriteLine($"    COMPILE ERROR {error}");
		}
		if (expected != null)
			Console.WriteLine(failures == 0 ? "ModCompatScan expect: PASS" : $"ModCompatScan expect: FAIL ({failures})");
		return failures == 0 ? 0 : 1;
	}

	/// <summary>The override and placement-file parsing the plugin applies, with the cases that matter.</summary>
	private static int SelfTest()
	{
		int failed = 0;
		void Check(bool condition, string what)
		{
			Console.WriteLine((condition ? "PASS " : "FAIL ") + what);
			if (!condition)
				failed++;
		}
		(bool Ok, ModCompatClass Class, ModPlacement Placement) Parse(string text, ModCompatClass fallback = ModCompatClass.B) =>
			(ModCompatScanner.TryParseOverride(text, fallback, out ModCompatClass c, out ModPlacement p, out _), c, p);

		Check(Parse("C") == (true, ModCompatClass.C, ModPlacement.WaOnly), "class C alone means wa-only");
		Check(Parse("D") == (true, ModCompatClass.D, ModPlacement.Adapted), "class D alone means adapted");
		Check(Parse("b") == (true, ModCompatClass.B, ModPlacement.Everywhere), "class letters are case-insensitive");
		Check(Parse("C everywhere") == (true, ModCompatClass.C, ModPlacement.Everywhere), "an explicit placement wins over the class default");
		Check(Parse("wa-only", ModCompatClass.D) == (true, ModCompatClass.D, ModPlacement.WaOnly), "a placement alone keeps the automatic class");
		Check(Parse("nodes").Placement == ModPlacement.Everywhere, "nodes is a synonym of everywhere");
		Check(!Parse("E").Ok && !Parse("1").Ok && !Parse("C wa-only extra").Ok && !Parse("").Ok && !Parse("wa-only C").Ok,
			"unknown class, digit, trailing word, empty and placement-before-class are rejected");

		var rejected = new List<(int Number, string Line, string Reason)>();
		var entries = ModCompatScanner.ParsePlacementFile(new[]
		{
			"# admin placement", "", "1521905890 C wa-only", "3154371364.sbm everywhere", "500818376", "1521905890 B",
		}, rejected);
		Check(entries.Count == 2 && entries["1521905890"] == ("C wa-only", 3, "1521905890 C wa-only"), "placement file: entry with its line number");
		Check(entries.ContainsKey("3154371364"), "placement file: the .sbm folder suffix is stripped");
		Check(rejected.Count == 2 && rejected[0] == (5, "500818376", "no verdict") && rejected[1].Reason == "duplicate mod id",
			"placement file: a line without a verdict and a duplicate id are rejected, first entry kept");
		Check(ModCompatScanner.ReadAuthorManifest(new[] { "# comment", "  ", " D adapted " }) == "D adapted", "author manifest: first non-comment line, trimmed");
		Check(ModCompatScanner.Scan(new List<Assembly>()).Class == ModCompatClass.A, "no assemblies is class A");
		Console.WriteLine(failed == 0 ? "ModCompatScan selftest: PASS" : $"ModCompatScan selftest: FAIL ({failed})");
		return failed == 0 ? 0 : 1;
	}

	/// <summary>The runtime's own assemblies first, then every managed DS64 assembly the runtime does not provide.</summary>
	private static List<MetadataReference> References(string dedicated64)
	{
		var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator))
			paths.TryAdd(Path.GetFileNameWithoutExtension(path), path);
		foreach (string path in Directory.GetFiles(dedicated64, "*.dll"))
		{
			if (paths.ContainsKey(Path.GetFileNameWithoutExtension(path)))
				continue;
			using var stream = File.OpenRead(path);
			using var reader = new PEReader(stream);
			if (reader.HasMetadata)
				paths[Path.GetFileNameWithoutExtension(path)] = path;
		}
		return paths.Values.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList();
	}

	/// <summary>One assembly per Data/Scripts subfolder, as MyScriptManager.LoadScripts groups them.</summary>
	private static List<Assembly> Compile(string id, string mod, List<MetadataReference> references, out List<string> errors)
	{
		errors = new List<string>();
		var assemblies = new List<Assembly>();
		string scripts = Path.Combine(mod, "Data", "Scripts");
		if (!Directory.Exists(scripts))
			return assemblies;
		var groups = Directory.GetFiles(scripts, "*.cs", SearchOption.AllDirectories)
			.Where(file => Path.GetDirectoryName(file) != scripts)
			.GroupBy(file => Path.GetRelativePath(scripts, file).Split(Path.DirectorySeparatorChar)[0])
			.OrderBy(group => group.Key, StringComparer.Ordinal);
		foreach (var group in groups)
		{
			byte[] image = Emit($"{id}_{group.Key}", group.ToList(), references, out List<string> groupErrors);
			if (image == null)
			{
				errors.AddRange(groupErrors.Select(error => $"{group.Key}: {error}"));
				continue;
			}
			assemblies.Add(new AssemblyLoadContext($"{id}_{group.Key}").LoadFromStream(new MemoryStream(image)));
		}
		return assemblies;
	}

	/// <summary>The game's parse options (C# 6, its conditional symbols on 1.210); a mod that only compiles with a
	/// newer language version, as some plugin loaders allow, is retried with the latest one.</summary>
	private static byte[] Emit(string name, List<string> files, List<MetadataReference> references, out List<string> errors)
	{
		errors = null;
		foreach (LanguageVersion version in new[] { LanguageVersion.CSharp6, LanguageVersion.Latest })
		{
			var options = new CSharpParseOptions(version, DocumentationMode.None)
				.WithPreprocessorSymbols("BRANCH_STABLE", "STABLE", "VERSION_210", "BUILD_14");
			var trees = files.Select(file => CSharpSyntaxTree.ParseText(UsingHeader + File.ReadAllText(file), options, file)).ToList();
			CSharpCompilation compilation = CSharpCompilation.Create(name, trees, references,
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, platform: Platform.X64));
			using var image = new MemoryStream();
			var emitted = compilation.Emit(image);
			if (emitted.Success)
				return image.ToArray();
			errors ??= emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
				.Select(diagnostic => diagnostic.ToString()).ToList();
		}
		return null;
	}
}
