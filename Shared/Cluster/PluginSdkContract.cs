#nullable enable
using System.Globalization;

namespace ClusterProtocol;

/// <summary>
/// The Magnetar PluginSdk contract the cluster code needs. Magnetar declares its contract as
/// <c>[assembly: AssemblyMetadata("PluginSdkContract", "major.minor")]</c> in PluginSdk.dll: the minor
/// goes up when public API is added, the major when API is removed or changed. A PluginSdk with the
/// same major and an equal or higher minor runs the cluster. Shared by the plugins' runtime check
/// (<c>ClusterPluginHost</c>) and the registry's <c>--check-plugin-sdk</c>.
/// </summary>
internal static class PluginSdkContract
{
    internal const string Key = "PluginSdkContract";

    // Raise when the cluster starts to use PluginSdk API added in a later contract.
    internal const int Major = 1;
    internal const int Minor = 1;

    internal static string Required => Major + "." + Minor;

    /// <summary>True when the declared "major.minor" contract runs the cluster; false when it doesn't or is malformed.</summary>
    internal static bool IsSatisfiedBy(string declared)
    {
        string[] parts = declared.Split('.');
        return parts.Length == 2
            && int.TryParse(
                parts[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int major
            )
            && int.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int minor
            )
            && major == Major
            && minor >= Minor;
    }
}
