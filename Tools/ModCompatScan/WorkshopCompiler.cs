using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ModCompatScan;

internal static class WorkshopCompiler
{
    // MyScriptCompiler.InjectMod assigns a line-number tag to bare [ProtoMember] attributes.
    private static readonly Func<CSharpCompilation, SyntaxTree, SyntaxTree> RewriteProtoTags =
        Assembly
            .Load("VRage.Scripting")
            .GetType("VRage.Scripting.Rewriters.ProtoTagRewriter", throwOnError: true)
            .GetMethod("Rewrite", BindingFlags.Public | BindingFlags.Static)
            .CreateDelegate<Func<CSharpCompilation, SyntaxTree, SyntaxTree>>();

    internal static CSharpCompilation WithProtoTags(CSharpCompilation compilation)
    {
        var trees = compilation.SyntaxTrees.Select(tree => RewriteProtoTags(compilation, tree));
        return compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(trees);
    }
}
