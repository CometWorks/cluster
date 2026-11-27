using System.Xml;
using HarmonyLib;
using VRage;
using VRage.Library.Collections;
using VRage.Serialization;
using VRage.Utils;

// The stock game assemblies target .NET Framework; the server runs them on .NET 10 with the
// dotnet-compat plugin patching the incompatibilities. This tool runs the same assemblies without
// Magnetar, so it carries the one patch its code paths need, plus a counter of the subtype hashes
// it can't name.
internal static class CompatPatches
{
    /// <summary>Non-zero MyStringHash values read from binary saves that no known string maps to.</summary>
    public static int UnresolvedHashes;

    public static void Apply()
    {
        var harmony = new Harmony("MagnetarWorld");
        harmony.Patch(
            AccessTools.Method(typeof(CustomRootWriter), nameof(CustomRootWriter.Init)),
            prefix: new HarmonyMethod(typeof(CompatPatches), nameof(CustomRootWriterInit))
        );
        harmony.Patch(
            AccessTools.DeclaredMethod(
                typeof(MySerializerMyStringHash),
                "Read",
                [typeof(BitStream), typeof(MyStringHash).MakeByRefType(), typeof(MySerializeInfo)]
            ),
            prefix: new HarmonyMethod(typeof(CompatPatches), nameof(StringHashRead))
        );
    }

    // The stock reader turns an unknown hash into the empty one without a word; count those.
    private static bool StringHashRead(BitStream stream, out MyStringHash value)
    {
        int hash = stream.ReadInt32();
        value = MyStringHash.TryGet(hash);
        if (hash != 0 && value == MyStringHash.NullOrEmpty)
        {
            Interlocked.Increment(ref UnresolvedHashes);
            lock (UnresolvedSamples)
                if (UnresolvedSamples.Count < 10)
                    UnresolvedSamples.Add(hash);
        }
        return false;
    }

    public static readonly SortedSet<int> UnresolvedSamples = new();

    public static string Unresolved() =>
        UnresolvedHashes
        + " subtype hashes ("
        + string.Join(", ", UnresolvedSamples.Select(h => "0x" + h.ToString("X8")))
        + (UnresolvedSamples.Count < UnresolvedHashes ? ", ..." : "")
        + ")";

    // XmlWellFormedWriter rejects the qualified name "xsi:type" as a local name; write it as a
    // prefixed attribute in the XML Schema instance namespace instead.
    private static bool CustomRootWriterInit(
        CustomRootWriter __instance,
        string customRootType,
        XmlWriter target
    )
    {
        __instance.m_target = target;
        __instance.m_customRootType = customRootType;
        target.WriteAttributeString(
            "xsi",
            "type",
            "http://www.w3.org/2001/XMLSchema-instance",
            customRootType
        );
        __instance.m_currentDepth = 0;
        return false;
    }
}
