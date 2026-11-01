using System;
using System.Reflection;
using System.Xml.Serialization;
using ClusterPlugin;
using HarmonyLib;
using VRage;

namespace Shared.Patches;

/// <summary>
/// Thread-safe lazy XML serializer registration. GetOrCreateSerializer
/// registered a type on first use by mutating three plain Dictionaries with no lock. The round-trip
/// gate's chunked compare (ImportRoundTrip.PrepareSource / ResidualsAgainstPrepared) serialises several
/// sector documents in parallel, and two chunks meeting a type for the first time raced straight into
/// Dictionary.Add ("An item with the same key has already been added. Key: MyObjectBuilder_EntityBase").
/// A plugin cannot retype the static fields to ConcurrentDictionary, so every public reader and both registration entry points take one lock
/// instead. The hit path pays an uncontended Monitor (XML serialization is nowhere near per-frame).
/// Vanilla, lock-free behaviour on an unclustered process.
/// </summary>
[HarmonyPatch(typeof(MyXmlSerializerManager))]
public static class MyXmlSerializerManagerPatch
{
    private static readonly object RegisterLock = new();

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyXmlSerializerManager.RegisterSerializer))]
    private static bool RegisterSerializerPrefix(Type type)
    {
        if (!ClusterRuntime.IsClustered)
            return true;
        lock (RegisterLock)
        {
            if (!MyXmlSerializerManager.m_serializersByType.ContainsKey(type))
                MyXmlSerializerManager.RegisterType(type, forceRegister: true, checkAttributes: false);
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyXmlSerializerManager.RegisterFromAssembly))]
    private static bool RegisterFromAssemblyPrefix(Assembly assembly)
    {
        if (!ClusterRuntime.IsClustered)
            return true;
        lock (RegisterLock)
        {
            if (assembly == null || MyXmlSerializerManager.m_registeredAssemblies.Contains(assembly))
                return false;
            MyXmlSerializerManager.m_registeredAssemblies.Add(assembly);
            foreach (var type in assembly.GetTypes())
            {
                try
                {
                    if (!MyXmlSerializerManager.m_serializersByType.ContainsKey(type))
                        MyXmlSerializerManager.RegisterType(type);
                }
                catch (Exception innerException)
                {
                    throw new InvalidOperationException("Error creating XML serializer for type " + type.Name, innerException);
                }
            }
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyXmlSerializerManager.GetOrCreateSerializer))]
    private static bool GetOrCreateSerializerPrefix(Type type, ref XmlSerializer __result)
    {
        if (!ClusterRuntime.IsClustered)
            return true;
        lock (RegisterLock)
        {
            __result = MyXmlSerializerManager.m_serializersByType.TryGetValue(type, out var value)
                ? value
                : MyXmlSerializerManager.RegisterType(type, forceRegister: true);
        }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyXmlSerializerManager.GetSerializer), new[] { typeof(Type) })]
    private static bool GetSerializerByTypePrefix(Type type, ref XmlSerializer __result)
    {
        if (!ClusterRuntime.IsClustered)
            return true;
        lock (RegisterLock)
            __result = MyXmlSerializerManager.m_serializersByType[type];
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyXmlSerializerManager.GetSerializedName))]
    private static bool GetSerializedNamePrefix(Type type, ref string __result)
    {
        if (!ClusterRuntime.IsClustered)
            return true;
        lock (RegisterLock)
            __result = MyXmlSerializerManager.m_serializedNameByType[type];
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyXmlSerializerManager.TryGetSerializer))]
    private static bool TryGetSerializerPrefix(string serializedName, ref XmlSerializer serializer, ref bool __result)
    {
        if (!ClusterRuntime.IsClustered)
            return true;
        lock (RegisterLock)
            __result = MyXmlSerializerManager.m_serializersBySerializedName.TryGetValue(serializedName, out serializer);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyXmlSerializerManager.GetSerializer), new[] { typeof(string) })]
    private static bool GetSerializerByNamePrefix(string serializedName, ref XmlSerializer __result)
    {
        if (!ClusterRuntime.IsClustered)
            return true;
        lock (RegisterLock)
            __result = MyXmlSerializerManager.m_serializersBySerializedName[serializedName];
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(MyXmlSerializerManager.IsSerializerAvailable))]
    private static bool IsSerializerAvailablePrefix(string name, ref bool __result)
    {
        if (!ClusterRuntime.IsClustered)
            return true;
        lock (RegisterLock)
            __result = MyXmlSerializerManager.m_serializersBySerializedName.ContainsKey(name);
        return false;
    }
}
