using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ClusterPlugin;

/// <summary>
/// SE-1727: finds Essentials by its command module, which has the !ess grids list GridListCommand replaces, not by its
/// assembly name. Magnetar compiles a hub plugin into an assembly named after its cache folder plus a random suffix
/// (Essentials-f130e28c_3ld0yvit.sqr), so only a local build is named Essentials.
/// </summary>
internal static class EssentialsAssembly
{
    internal const string CommandsType = "ServerPlugin.Commands.EssentialsModule";

    internal static bool IsLoaded(IEnumerable<Assembly> assemblies) => assemblies.Any(Defines);

    private static bool Defines(Assembly assembly)
    {
        try
        {
            return assembly.GetType(CommandsType, false) != null;
        }
        catch (System.Exception)
        {
            return false; // an assembly that cannot resolve its own types is not Essentials
        }
    }
}
