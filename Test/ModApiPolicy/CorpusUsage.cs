using System.IO.Compression;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        foreach (string mod in Directory.EnumerateDirectories(root).Order())
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            bool hasScripts = false;
            foreach (var group in Scripts(mod).GroupBy(s => s.Path.Split('/')[0]))
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

    internal static IEnumerable<string> Members(SemanticModel model)
    {
        var nodes = model
            .SyntaxTree.GetRoot()
            .DescendantNodes()
            .Where(n =>
                n
                    is SimpleNameSyntax
                        or ElementAccessExpressionSyntax
                        or ObjectCreationExpressionSyntax
                        or BinaryExpressionSyntax
                        or PrefixUnaryExpressionSyntax
                        or PostfixUnaryExpressionSyntax
                        or AssignmentExpressionSyntax
            );
        foreach (var node in nodes)
        {
            var symbol = model.GetSymbolInfo(node).Symbol;
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
                !symbol.ContainingAssembly.Equals(model.Compilation.Assembly)
                && symbol.ContainingNamespace.ToDisplayString() is string ns
                && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal))
            )
                yield return "System.*";
        }
    }

    private static IEnumerable<(string Path, string Text)> Scripts(string mod)
    {
        foreach (
            var data in Directory
                .EnumerateDirectories(mod)
                .Where(d => Path.GetFileName(d).Equals("Data", StringComparison.OrdinalIgnoreCase))
        )
        foreach (
            var scripts in Directory
                .EnumerateDirectories(data)
                .Where(d =>
                    Path.GetFileName(d).Equals("Scripts", StringComparison.OrdinalIgnoreCase)
                )
        )
        foreach (
            var file in Directory
                .EnumerateFiles(scripts, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetExtension(f).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        )
            yield return (
                Path.GetRelativePath(scripts, file).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllText(file)
            );
        foreach (var file in Directory.EnumerateFiles(mod, "*_legacy.bin"))
        {
            using var zip = ZipFile.OpenRead(file);
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (
                    !path.StartsWith("Data/Scripts/", StringComparison.OrdinalIgnoreCase)
                    || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                )
                    continue;
                using var reader = new StreamReader(entry.Open());
                yield return (path[13..], reader.ReadToEnd());
            }
        }
    }
}
