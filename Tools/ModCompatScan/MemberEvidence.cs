using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;

namespace ModCompatScan;

internal sealed class MemberEvidence : IModScanEvidence
{
    public Dictionary<string, int> Members { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Writes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> IndirectAccess { get; } = new(StringComparer.Ordinal);
    public HashSet<string> VslDelegateFields { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> UpdateMembers { get; } = new(StringComparer.Ordinal);
    public int EntityCollections { get; private set; }
    public int MutableLogicFields { get; private set; }
    public int UnresolvedTypes { get; set; }

    public void Field(FieldInfo field, bool sessionComponent, bool logicComponent)
    {
        if (logicComponent && !field.IsStatic && !field.IsInitOnly && !field.IsLiteral)
            MutableLogicFields++;
        if ((sessionComponent || field.IsStatic) && IsEntityCollection(field.FieldType))
            EntityCollections++;
    }

    private static bool IsEntityCollection(Type type)
    {
        if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            return false;
        var arguments = type.IsArray ? new[] { type.GetElementType() } : type.GetGenericArguments();
        return arguments.Any(t =>
            t.FullName == "VRage.ModAPI.IMyEntity"
            || t.GetInterfaces().Any(i => i.FullName == "VRage.ModAPI.IMyEntity")
            || IsEntityCollection(t)
        );
    }

    public void Site(MemberInfo target, MethodBase caller, bool sessionComponent, OpCode code)
    {
        var type = target.DeclaringType;
        if (type == null || type.Assembly == caller.Module.Assembly)
            return;
        string assembly = type.Assembly.GetName().Name;
        string key;
        if (
            assembly == "System.Private.CoreLib"
            || assembly.StartsWith("System.", StringComparison.Ordinal)
        )
            key = "System.*";
        else if (type.Namespace == "VRageMath")
            key = "VRageMath.*";
        else if (
            assembly == "VRage"
            || assembly.StartsWith("VRage.", StringComparison.Ordinal)
            || assembly.StartsWith("Sandbox.", StringComparison.Ordinal)
            || assembly.StartsWith("SpaceEngineers.", StringComparison.Ordinal)
        )
            key = MemberKey(target);
        else
            return;
        RecordAccess(target, code, key);
        Members[key] = Members.GetValueOrDefault(key) + 1;
        if (
            sessionComponent
            && caller.Name is "UpdateAfterSimulation" or "UpdateBeforeSimulation" or "Simulate"
        )
            UpdateMembers[key] = UpdateMembers.GetValueOrDefault(key) + 1;
    }

    private void RecordAccess(MemberInfo target, OpCode code, string key)
    {
        Type value;
        if (target is FieldInfo field)
        {
            value = field.FieldType;
            if (
                field.DeclaringType.FullName == "Sandbox.Game.MyVisualScriptLogicProvider"
                && typeof(MulticastDelegate).IsAssignableFrom(value)
            )
                VslDelegateFields.Add(key);
            if (code == OpCodes.Stfld || code == OpCodes.Stsfld)
            {
                Writes[key] = Writes.GetValueOrDefault(key) + 1;
                return;
            }
        }
        else if (target is MethodInfo { IsSpecialName: true } method)
        {
            if (
                method.Name.StartsWith("set_")
                || method.Name.StartsWith("add_")
                || method.Name.StartsWith("remove_")
            )
            {
                Writes[key] = Writes.GetValueOrDefault(key) + 1;
                return;
            }
            if (!method.Name.StartsWith("get_"))
                return;
            value = method.ReturnType;
        }
        else
            return;
        // An address or a returned mutable object may be written through a later instruction.
        if (
            code == OpCodes.Ldflda
            || code == OpCodes.Ldsflda
            || value.IsByRef
            || (!value.IsValueType && value != typeof(string))
        )
            IndirectAccess[key] = IndirectAccess.GetValueOrDefault(key) + 1;
    }

    private static string MemberKey(MemberInfo target)
    {
        var type = target.DeclaringType;
        if (type.IsConstructedGenericType)
        {
            type = type.GetGenericTypeDefinition();
            target = target.Module.ResolveMember(target.MetadataToken);
        }
        string owner = type.FullName.Replace('+', '.');
        if (type.IsEnum || typeof(MulticastDelegate).IsAssignableFrom(type))
            return owner + ".*";
        if (target is not MethodBase method)
            return owner + "." + target.Name;
        if (method is MethodInfo { IsGenericMethod: true } generic)
            method = generic.GetGenericMethodDefinition();
        string name = method.Name;
        var parameters = method
            .GetParameters()
            .Select(p => ParameterName(p.ParameterType))
            .ToList();
        if (method.IsSpecialName && (name.StartsWith("add_") || name.StartsWith("remove_")))
            return owner + "." + name[(name.IndexOf('_') + 1)..];
        if (method.IsSpecialName && (name.StartsWith("get_") || name.StartsWith("set_")))
        {
            if (name.StartsWith("set_"))
                parameters.RemoveAt(parameters.Count - 1);
            return owner
                + "."
                + name[4..]
                + (parameters.Count == 0 ? "" : "(" + string.Join(",", parameters) + ")");
        }
        if (method.IsGenericMethod)
            name += "``" + method.GetGenericArguments().Length;
        string key =
            owner + "." + name.Replace('.', '#') + "(" + string.Join(",", parameters) + ")";
        if (name is "op_Implicit" or "op_Explicit")
            key += "~" + ParameterName(((MethodInfo)method).ReturnType);
        return key;
    }

    private static string ParameterName(Type type)
    {
        if (type.IsGenericParameter)
            return (type.DeclaringMethod == null ? "`" : "``") + type.GenericParameterPosition;
        if (type.IsByRef || type.IsPointer)
            return ParameterName(type.GetElementType()) + (type.IsByRef ? "@" : "*");
        if (type.IsArray)
            return ParameterName(type.GetElementType())
                + (
                    type.IsSZArray
                        ? "[]"
                        : "[" + string.Join(",", Enumerable.Repeat("0:", type.GetArrayRank())) + "]"
                );
        if (!type.IsGenericType)
            return type.FullName.Replace('+', '.');
        var args = new Queue<string>(type.GetGenericArguments().Select(ParameterName));
        return string.Join(
            ".",
            type.GetGenericTypeDefinition()
                .FullName.Split('+')
                .Select(part =>
                {
                    int tick = part.IndexOf('`');
                    return tick < 0
                        ? part
                        : part[..tick]
                            + "{"
                            + string.Join(
                                ",",
                                Enumerable
                                    .Range(0, int.Parse(part[(tick + 1)..]))
                                    .Select(_ => args.Dequeue())
                            )
                            + "}";
                })
        );
    }
}
