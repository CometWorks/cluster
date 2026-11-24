using Microsoft.CodeAnalysis;

internal static class GameSurface
{
    internal static bool IsGame(IAssemblySymbol assembly)
    {
        var name = assembly.Name;
        return name == "VRage"
            || name.StartsWith("VRage.", StringComparison.Ordinal)
            || name.StartsWith("Sandbox.", StringComparison.Ordinal)
            || name.StartsWith("SpaceEngineers.", StringComparison.Ordinal);
    }

    private static readonly string[] Roots =
    [
        "Sandbox.ModAPI",
        "VRage.Game.ModAPI",
        "VRage.ModAPI",
        "SpaceEngineers.Game.ModAPI",
    ];

    internal static bool IsInterfaceSurface(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace.ToDisplayString();
        return Roots.Any(root => ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal))
            || type.ToDisplayString() == "Sandbox.Game.MyVisualScriptLogicProvider";
    }

    internal static bool DeclaredMember(ISymbol member) =>
        member.DeclaredAccessibility == Accessibility.Public
            && member is IPropertySymbol or IFieldSymbol or IEventSymbol
        || member
            is IMethodSymbol
            {
                DeclaredAccessibility: Accessibility.Public,
                MethodKind: MethodKind.Ordinary
            };

    internal static string Key(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { ReducedFrom: not null } method)
            symbol = method.ReducedFrom;
        if (symbol is IMethodSymbol { AssociatedSymbol: not null } accessor)
            symbol = accessor.AssociatedSymbol;
        symbol = symbol.OriginalDefinition;
        var type = symbol.ContainingType;
        if (type.TypeKind is TypeKind.Enum or TypeKind.Delegate)
            return type.GetDocumentationCommentId()[2..] + ".*";
        string id = symbol.GetDocumentationCommentId()[2..];
        return symbol is IMethodSymbol { Parameters.Length: 0 } ? id + "()" : id;
    }

    internal static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol parent)
    {
        foreach (var child in parent.GetMembers().OfType<INamespaceOrTypeSymbol>())
        {
            if (child is INamedTypeSymbol t)
                yield return t;
            foreach (var type in Types(child))
                yield return type;
        }
    }
}
