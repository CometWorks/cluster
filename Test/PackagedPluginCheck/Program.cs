using System.Reflection;
using System.Runtime.Loader;

// Match Magnetar local-plugin discovery without starting the game or invoking plugins.
// Resolve platform assemblies only; each plugin must supply its own runtime dependencies.
if (args.Length != 3) throw new ArgumentException("Usage: PackagedPluginCheck PLUGIN_DLL DEDICATED64 MAGNETAR_SDK");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string? path = args.Skip(1).Select(root => Path.Combine(root, name.Name + ".dll")).FirstOrDefault(File.Exists);
    return path is null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
};
try
{
    var plugin = Assembly.LoadFrom(Path.GetFullPath(args[0]));
    Console.WriteLine($"{plugin.GetName().Name}: discovered {plugin.GetTypes().Length} types");
    return 0;
}
catch (ReflectionTypeLoadException error)
{
    foreach (var failure in error.LoaderExceptions) Console.Error.WriteLine(failure?.Message);
    return 1;
}
