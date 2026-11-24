using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ModCompatScan;

internal static class WorkshopCompiler
{
    // DS 1.211.018: MyScriptManager.UpdateCompatibility applies the current game's
    // substitutions to source text, including old namespaces still used in Workshop mods.
    private static readonly Dictionary<string, string> CompatibilityChanges =
        (Dictionary<string, string>)
            Assembly
                .Load("Sandbox.Game")
                .GetType("Sandbox.Game.World.MyScriptManager", throwOnError: true)
                .GetField("m_compatibilityChanges", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);

    // MyScriptCompiler.InjectMod assigns a line-number tag to bare [ProtoMember] attributes.
    private static readonly Func<CSharpCompilation, SyntaxTree, SyntaxTree> RewriteProtoTags =
        Assembly
            .Load("VRage.Scripting")
            .GetType("VRage.Scripting.Rewriters.ProtoTagRewriter", throwOnError: true)
            .GetMethod("Rewrite", BindingFlags.Public | BindingFlags.Static)
            .CreateDelegate<Func<CSharpCompilation, SyntaxTree, SyntaxTree>>();

    internal static string PrepareSource(string text)
    {
        text = ScriptReferences.UsingHeader + text;
        foreach (var change in CompatibilityChanges)
            text = text.Replace(change.Key, change.Value);
        return text;
    }

    internal static CSharpCompilation WithProtoTags(CSharpCompilation compilation)
    {
        var trees = compilation.SyntaxTrees.Select(tree => RewriteProtoTags(compilation, tree));
        return compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(trees);
    }
}
