using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using ModCompatScan;

internal static class CorpusUsage
{
    internal static Dictionary<string, int> Read(string root, CSharpCompilation empty)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int groups = 0,
            partial = 0,
            scripted = 0;
        var options = ScriptReferences.ParseOptions(empty);
        foreach (string mod in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            bool hasScripts = false;
            // The groups ModCompatScan compiles: the first path component below Data/Scripts.
            foreach (var group in WorkshopScripts.Scripts(mod).GroupBy(s => s.Path.Split('/')[0]))
            {
                hasScripts = true;
                groups++;
                var trees = group
                    .Select(s =>
                        CSharpSyntaxTree.ParseText(
                            ScriptReferences.UsingHeader + s.Text,
                            options,
                            s.Path
                        )
                    )
                    .ToList();
                var compilation = empty.AddSyntaxTrees(trees);
                if (compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                    partial++;
                foreach (var tree in trees)
                    used.UnionWith(Members(compilation.GetSemanticModel(tree)));
            }
            foreach (var key in used)
                counts[key] = counts.GetValueOrDefault(key) + 1;
            if (hasScripts)
                scripted++;
        }
        Console.Error.WriteLine(
            $"Scripted mods {scripted}, script groups {groups}, groups with compiler errors {partial}, resolved game members {counts.Count}"
        );
        if (scripted == 0)
            throw new InvalidDataException($"No mod scripts under {root}");
        return counts;
    }

    /// <summary>
    /// Every game member the compiled IL of this file references: names in the source, and the calls the compiler
    /// adds (implicit base constructors, user-defined conversions and operators, collection initializer Add,
    /// foreach GetEnumerator/MoveNext/Current/Dispose). Constants and enum values also count, although the IL
    /// inlines them.
    /// </summary>
    internal static IEnumerable<string> Members(SemanticModel model)
    {
        var root = model.SyntaxTree.GetRoot();
        var symbols = new List<ISymbol>();
        foreach (
            var node in root.DescendantNodes()
                .Where(n =>
                    n
                        is SimpleNameSyntax
                            or ElementAccessExpressionSyntax
                            or ObjectCreationExpressionSyntax
                            or BinaryExpressionSyntax
                            or PrefixUnaryExpressionSyntax
                            or PostfixUnaryExpressionSyntax
                            or AssignmentExpressionSyntax
                )
        )
            symbols.Add(model.GetSymbolInfo(node).Symbol);
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case CommonForEachStatementSyntax loop:
                    var info = model.GetForEachStatementInfo(loop);
                    symbols.Add(info.GetEnumeratorMethod);
                    symbols.Add(info.MoveNextMethod);
                    symbols.Add(info.CurrentProperty);
                    symbols.Add(info.DisposeMethod);
                    break;
                case TypeDeclarationSyntax type
                    when model.GetDeclaredSymbol(type) is INamedTypeSymbol declared:
                    // A class without a constructor gets one that calls the base constructor.
                    if (declared.InstanceConstructors.Any(c => c.IsImplicitlyDeclared))
                        symbols.Add(BaseConstructor(declared));
                    break;
                // So does a constructor without ": base(...)" or ": this(...)".
                case ConstructorDeclarationSyntax { Initializer: null } ctor
                    when !ctor.Modifiers.Any(SyntaxKind.StaticKeyword)
                        && model.GetDeclaredSymbol(ctor) is IMethodSymbol declared:
                    symbols.Add(BaseConstructor(declared.ContainingType));
                    break;
            }
            // Operation roots: member bodies, initializers and constructor initializers.
            if (
                node
                    is BaseMethodDeclarationSyntax
                        or AccessorDeclarationSyntax
                        or ArrowExpressionClauseSyntax
                        {
                            Parent: PropertyDeclarationSyntax or IndexerDeclarationSyntax
                        }
                        or EqualsValueClauseSyntax
                        {
                            Parent: PropertyDeclarationSyntax
                                or VariableDeclaratorSyntax
                                {
                                    Parent.Parent: BaseFieldDeclarationSyntax
                                }
                        }
                && model.GetOperation(node) is IOperation operation
            )
            {
                foreach (var op in operation.DescendantsAndSelf())
                    symbols.Add(Target(op));
            }
        }
        foreach (var symbol in symbols)
        {
            if (
                symbol is not (IPropertySymbol or IFieldSymbol or IEventSymbol or IMethodSymbol)
                || symbol.ContainingType == null
                || symbol.ContainingAssembly == null
            )
                continue;
            if (GameSurface.IsGame(symbol.ContainingAssembly))
            {
                var key = GameSurface.Key(symbol);
                yield return key.StartsWith("VRageMath.", StringComparison.Ordinal)
                    ? "VRageMath.*"
                    : key;
            }
            else if (
                !SymbolEqualityComparer.Default.Equals(
                    symbol.ContainingAssembly,
                    model.Compilation.Assembly
                )
                && symbol.ContainingNamespace.ToDisplayString() is string ns
                && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal))
            )
                yield return "System.*";
        }
    }

    private static ISymbol Target(IOperation op) =>
        op switch
        {
            IInvocationOperation call => call.TargetMethod,
            IObjectCreationOperation create => create.Constructor,
            IMemberReferenceOperation reference => reference.Member,
            IConversionOperation conversion => conversion.OperatorMethod,
            IBinaryOperation binary => binary.OperatorMethod,
            IUnaryOperation unary => unary.OperatorMethod,
            ICompoundAssignmentOperation compound => compound.OperatorMethod,
            IIncrementOrDecrementOperation step => step.OperatorMethod,
            _ => null,
        };

    private static IMethodSymbol BaseConstructor(INamedTypeSymbol type) =>
        type.TypeKind == TypeKind.Class
            ? type.BaseType?.InstanceConstructors.FirstOrDefault(c =>
                c.Parameters.All(p => p.IsOptional || p.IsParams)
            )
            : null;
}
