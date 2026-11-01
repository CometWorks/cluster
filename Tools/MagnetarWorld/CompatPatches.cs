using System.Xml;
using HarmonyLib;
using VRage;

// The stock game assemblies target .NET Framework; the server runs them on .NET 10 with the
// dotnet-compat plugin patching the incompatibilities. This tool runs the same assemblies without
// Magnetar, so it carries the one patch its code paths need.
internal static class CompatPatches
{
	public static void Apply()
	{
		var harmony = new Harmony("MagnetarWorld");
		harmony.Patch(AccessTools.Method(typeof(CustomRootWriter), nameof(CustomRootWriter.Init)),
			prefix: new HarmonyMethod(typeof(CompatPatches), nameof(CustomRootWriterInit)));
	}

	// XmlWellFormedWriter rejects the qualified name "xsi:type" as a local name; write it as a
	// prefixed attribute in the XML Schema instance namespace instead.
	private static bool CustomRootWriterInit(CustomRootWriter __instance, string customRootType, XmlWriter target)
	{
		__instance.m_target = target;
		__instance.m_customRootType = customRootType;
		target.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", customRootType);
		__instance.m_currentDepth = 0;
		return false;
	}
}
