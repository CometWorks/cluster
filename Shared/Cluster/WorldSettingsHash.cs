#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace ClusterProtocol;

/// <summary>
/// SE-796: the hash a node and the Gateway compare at admission to agree on a world's session settings and mods.
/// It covers what the checkpoint says, not its bytes: Quasar and managed preparation both rewrite Sandbox.sbc when a
/// profile changes, with different XML declarations, formatting and order of settings.
/// </summary>
internal static class WorldSettingsHash
{
    public static string Of(string checkpoint)
    {
        XElement root = XDocument.Load(checkpoint).Root ?? throw new InvalidOperationException("World checkpoint is empty: " + checkpoint);
        StringBuilder text = new StringBuilder();
        foreach (XElement setting in Children(root, "Settings").OrderBy(x => x.Name.LocalName, StringComparer.Ordinal))
            Append(text, setting);
        text.Append('\0');
        // The order of the mod list is the game's load order, so it counts.
        foreach (XElement mod in Children(root, "Mods"))
            Append(text, mod);
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
    }

    private static XElement[] Children(XElement root, string name)
        => root.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Elements().ToArray() ?? [];

    private static void Append(StringBuilder text, XElement element)
    {
        text.Append('<').Append(element.Name.LocalName);
        foreach (XAttribute attribute in element.Attributes().Where(x => !x.IsNamespaceDeclaration)
                     .OrderBy(x => x.Name.ToString(), StringComparer.Ordinal))
            text.Append('\u0001').Append(attribute.Name).Append('=').Append(attribute.Value);
        text.Append('>');
        if (element.HasElements)
            foreach (XElement child in element.Elements())
                Append(text, child);
        else
            text.Append(element.Value.Trim());
        text.Append("</>");
    }
}
