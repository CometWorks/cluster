using System.Runtime.Loader;

// A managed (Quasar) node needs a canonical configuration for every configuration type its local
// plugins load. Quasar exports one only for the profile's plugins, so the package carries the default
// of the bundled ones, serialized by the same PluginSdk the plugin was built against (SE-1078).
if (args.Length < 4)
{
    Console.Error.WriteLine("usage: PluginConfigDefaults PluginSdk.dll Plugin.dll ConfigType output.json [probe folder...]");
    return 2;
}
string[] probe = [Path.GetDirectoryName(Path.GetFullPath(args[0])), Path.GetDirectoryName(Path.GetFullPath(args[1])), .. args[4..]];
AssemblyLoadContext.Default.Resolving += (context, name) =>
    probe.Select(folder => Path.Combine(folder, name.Name + ".dll")).Where(File.Exists).Select(context.LoadFromAssemblyPath).FirstOrDefault();
var sdk = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
var type = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1])).GetType(args[2], throwOnError: true);
var save = sdk.GetType("PluginSdk.Config.ConfigStorage", throwOnError: true).GetMethod("SaveJson").MakeGenericMethod(type);
var envelope = System.Text.Json.Nodes.JsonNode.Parse((string)save.Invoke(null, [Activator.CreateInstance(type)]));
File.WriteAllText(args[3], new System.Text.Json.Nodes.JsonObject { ["configType"] = args[2], ["configuration"] = envelope }.ToJsonString() + "\n");
return 0;
