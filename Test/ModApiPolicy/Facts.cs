using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Input of generate.py: one tab-separated line per member that needs a policy row, sorted by key. Columns:
/// key, type (without generic arity), member (metadata name), symbol kind, type kind, assembly, api (1 for the
/// ModAPI surface), static, readonly, declaration (File.cs:line in the decompiled game code), implementation
/// (for an interface member: the declaration of the member that implements it), and the API interface members
/// a concrete member implements (space separated).
/// </summary>
internal static class Facts
{
    internal static void Write(
        CSharpCompilation empty,
        ISet<string> corpus,
        string decompiled,
        TextWriter output
    )
    {
        var members = new SortedDictionary<string, (ISymbol Symbol, bool Api)>(
            StringComparer.Ordinal
        );
        var allTypes = new List<INamedTypeSymbol>();
        foreach (
            var assembly in empty.SourceModule.ReferencedAssemblySymbols.Where(GameSurface.IsGame)
        )
        {
            foreach (var type in GameSurface.Types(assembly.GlobalNamespace))
            {
                allTypes.Add(type);
                // A corpus key can name a member of a non-public base type: the IL of a call through a
                // public derived type names the virtual member where it is declared.
                bool surface =
                    type.DeclaredAccessibility == Accessibility.Public
                    && GameSurface.IsInterfaceSurface(type);
                foreach (var member in type.GetMembers())
                {
                    if (
                        member
                        is not (IPropertySymbol or IFieldSymbol or IEventSymbol or IMethodSymbol)
                    )
                        continue;
                    string key = GameSurface.Key(member);
                    bool api = surface && GameSurface.DeclaredMember(member);
                    if (!api && !corpus.Contains(key))
                        continue;
                    if (!members.TryGetValue(key, out var known) || api && !known.Api)
                        members[key] = (Key(member), api);
                }
            }
        }

        var apiKeys = members.Where(m => m.Value.Api).Select(m => m.Key).ToHashSet();
        var implementations = new Dictionary<string, List<ISymbol>>(StringComparer.Ordinal);
        var implemented = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var type in allTypes.Where(t => t.TypeKind is TypeKind.Class or TypeKind.Struct))
        {
            foreach (var face in type.AllInterfaces)
            {
                foreach (var member in face.GetMembers())
                {
                    string faceKey = GameSurface.Key(member);
                    if (!apiKeys.Contains(faceKey))
                        continue;
                    var impl = type.FindImplementationForInterfaceMember(member);
                    if (impl == null || impl.ContainingType.TypeKind == TypeKind.Interface)
                        continue;
                    impl = Key(impl);
                    if (!implementations.TryGetValue(faceKey, out var list))
                        implementations[faceKey] = list = [];
                    if (!list.Contains(impl, SymbolEqualityComparer.Default))
                        list.Add(impl);
                    // An explicit implementation often forwards to a public member of the same name and
                    // signature, which is what a mod calls; that member keeps the interface contract too.
                    string signature = faceKey[
                        (
                            member
                                .ContainingType.OriginalDefinition.GetDocumentationCommentId()
                                .Length - 2
                        )..
                    ];
                    foreach (
                        var candidate in type.GetMembers(member.Name)
                            .Append(impl)
                            .Select(GameSurface.Key)
                            .Where(key =>
                                key == GameSurface.Key(impl)
                                || key.EndsWith(signature, StringComparison.Ordinal)
                            )
                            .Where(members.ContainsKey)
                    )
                    {
                        if (!implemented.TryGetValue(candidate, out var faces))
                            implemented[candidate] = faces = new SortedSet<string>(
                                StringComparer.Ordinal
                            );
                        faces.Add(faceKey);
                    }
                }
            }
        }

        // The implementation a reason cites: the least derived implementing type, then top-level before
        // nested, then by name, so the answer does not depend on the order types are enumerated in.
        var chosen = implementations.ToDictionary(
            pair => pair.Key,
            pair =>
                pair.Value.OrderBy(s => Depth(s.ContainingType))
                    .ThenBy(s => s.ContainingType.ContainingType == null ? 0 : 1)
                    .ThenBy(s => s.GetDocumentationCommentId(), StringComparer.Ordinal)
                    .First(),
            StringComparer.Ordinal
        );

        var wanted = members.Values.Select(m => m.Symbol).Concat(chosen.Values);
        var lines = Locate(empty, decompiled, wanted);
        foreach (var (key, (symbol, api)) in members)
        {
            var type = symbol.ContainingType;
            string declaration =
                (key.EndsWith(".*") ? null : Line(lines, symbol)) ?? Line(lines, type) ?? "";
            string implementation = chosen.TryGetValue(key, out var impl)
                ? Line(lines, impl) ?? ""
                : "";
            output.WriteLine(
                string.Join(
                    '\t',
                    key,
                    TypeName(type),
                    symbol.MetadataName,
                    symbol.Kind,
                    type.TypeKind,
                    type.ContainingAssembly.Name,
                    api ? "1" : "0",
                    symbol.IsStatic,
                    symbol is IFieldSymbol { IsReadOnly: true } or IFieldSymbol { IsConst: true }
                        ? "readonly"
                        : "",
                    declaration,
                    implementation,
                    implemented.TryGetValue(key, out var faces) ? string.Join(' ', faces) : ""
                )
            );
        }
    }

    /// <summary>Accessors stand for their property or event; members of constructed types for their definition.</summary>
    private static ISymbol Key(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { AssociatedSymbol: not null } accessor)
            symbol = accessor.AssociatedSymbol;
        return symbol.OriginalDefinition;
    }

    private static int Depth(INamedTypeSymbol type)
    {
        int depth = 0;
        for (var t = type.BaseType; t != null; t = t.BaseType)
            depth++;
        return depth;
    }

    private static string TypeName(INamedTypeSymbol type) =>
        System.Text.RegularExpressions.Regex.Replace(
            type.GetDocumentationCommentId()[2..],
            @"`\d+",
            ""
        );

    private static string Line(Dictionary<string, string> lines, ISymbol symbol) =>
        lines.GetValueOrDefault(symbol.GetDocumentationCommentId() ?? "");

    /// <summary>
    /// Finds the declarations in the decompiled code (one folder per assembly). The files that declare the wanted
    /// types are compiled together with the game assemblies as references, so overloads resolve to the same
    /// documentation IDs as the game's own symbols.
    /// </summary>
    private static Dictionary<string, string> Locate(
        CSharpCompilation empty,
        string decompiled,
        IEnumerable<ISymbol> symbols
    )
    {
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        var byAssembly = symbols
            .Select(s => s.ContainingType)
            .Select(Outermost)
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .GroupBy(t => t.ContainingAssembly.Name)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in byAssembly)
        {
            string folder = Path.Combine(decompiled, group.Key);
            if (!Directory.Exists(folder))
                continue;
            var files = TypeFiles(folder);
            var paths = group
                .Select(t => files.GetValueOrDefault(t.GetDocumentationCommentId()[2..]))
                .Where(p => p != null)
                .Distinct()
                .Order(StringComparer.Ordinal);
            var compilation = empty.AddSyntaxTrees(
                paths.Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), path: p))
            );
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var declarations = tree.GetRoot()
                    .DescendantNodes(n =>
                        n
                            is not (
                                BlockSyntax
                                or ArrowExpressionClauseSyntax
                                or EqualsValueClauseSyntax
                            )
                    )
                    .Where(n => n is MemberDeclarationSyntax or VariableDeclaratorSyntax);
                foreach (var node in declarations)
                {
                    if (model.GetDeclaredSymbol(node)?.GetDocumentationCommentId() is not string id)
                        continue;
                    int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    lines.TryAdd(id, $"{Path.GetFileName(tree.FilePath)}:{line}");
                }
            }
        }
        return lines;
    }

    private static INamedTypeSymbol Outermost(INamedTypeSymbol type)
    {
        while (type.ContainingType != null)
            type = type.ContainingType;
        return type.OriginalDefinition;
    }

    /// <summary>Metadata name of each top-level type, from the syntax of the files (folder names may be changed for case collisions).</summary>
    private static Dictionary<string, string> TypeFiles(string folder)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (
            string path in Directory
                .EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
        )
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
            foreach (
                var declaration in root.DescendantNodes(n =>
                        n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax
                    )
                    .OfType<MemberDeclarationSyntax>()
            )
            {
                string name;
                int arity;
                switch (declaration)
                {
                    case BaseTypeDeclarationSyntax t:
                        name = t.Identifier.Text;
                        arity =
                            (t as TypeDeclarationSyntax)?.TypeParameterList?.Parameters.Count ?? 0;
                        break;
                    case DelegateDeclarationSyntax d:
                        name = d.Identifier.Text;
                        arity = d.TypeParameterList?.Parameters.Count ?? 0;
                        break;
                    default:
                        continue;
                }
                string ns = string.Join(
                    '.',
                    declaration
                        .Ancestors()
                        .OfType<BaseNamespaceDeclarationSyntax>()
                        .Reverse()
                        .Select(n => n.Name.ToString())
                );
                string full =
                    (ns.Length > 0 ? ns + "." : "") + name + (arity > 0 ? "`" + arity : "");
                files.TryAdd(full, path);
            }
        }
        return files;
    }
}
