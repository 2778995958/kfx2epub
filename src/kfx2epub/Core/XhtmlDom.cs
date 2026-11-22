using System.Text;

namespace Kfx2Epub.Core;

// 輕量 XML DOM，刻意保留 lxml 的 text/tail 分離語意。
public sealed class XNode
{
    public string Name { get; set; }
    public string Namespace { get; set; }
    public string Text { get; set; }
    public string Tail { get; set; }
    public XNode Parent { get; internal set; }
    public List<XNode> Children { get; } = new();
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> NsMap { get; } = new(StringComparer.Ordinal);

    public XNode(string name, string ns = "")
    {
        Name = name;
        Namespace = ns;
    }

    public string Tag => Namespace.Length == 0 ? Name : "{" + Namespace + "}" + Name;

    public XNode Append(XNode child)
    {
        child.Parent = this;
        Children.Add(child);
        return child;
    }

    public XNode Insert(int index, XNode child)
    {
        child.Parent = this;
        Children.Insert(index, child);
        return child;
    }

    public void Remove()
    {
        Parent?.Children.Remove(this);
        Parent = null;
    }

    public void RemoveChild(XNode child)
    {
        if (Children.Remove(child)) child.Parent = null;
    }

    public int IndexInParent() => Parent?.Children.IndexOf(this) ?? -1;

    public XNode NextSibling()
    {
        var parent = Parent;
        if (parent is null) return null;
        var index = parent.Children.IndexOf(this);
        return index >= 0 && index + 1 < parent.Children.Count ? parent.Children[index + 1] : null;
    }

    public XNode PreviousSibling()
    {
        var parent = Parent;
        if (parent is null) return null;
        var index = parent.Children.IndexOf(this);
        return index > 0 ? parent.Children[index - 1] : null;
    }

    public IEnumerable<XNode> Ancestors()
    {
        var node = Parent;
        while (node is not null)
        {
            yield return node;
            node = node.Parent;
        }
    }

    public IEnumerable<XNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var descendant in child.Descendants()) yield return descendant;
        }
    }

    public IEnumerable<XNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var descendant in Descendants()) yield return descendant;
    }

    public IEnumerable<XNode> Elements() => Children;

    public IEnumerable<XNode> Elements(string localName)
        => Children.Where(c => c.Namespace.Length == 0 && c.Name == localName);

    public IEnumerable<XNode> ElementsNs(string ns, string localName)
        => Children.Where(c => c.Namespace == ns && c.Name == localName);

    public XNode Element(string localName)
        => Children.FirstOrDefault(c => c.Namespace.Length == 0 && c.Name == localName);

    public XNode ElementNs(string ns, string localName)
        => Children.FirstOrDefault(c => c.Namespace == ns && c.Name == localName);

    public XNode FindDescendant(string localName)
        => Descendants().FirstOrDefault(n => n.Namespace.Length == 0 && n.Name == localName);

    public XNode FindDescendantNs(string ns, string localName)
        => Descendants().FirstOrDefault(n => n.Namespace == ns && n.Name == localName);

    public IEnumerable<XNode> FindDescendants(string localName)
        => Descendants().Where(n => n.Namespace.Length == 0 && n.Name == localName);

    public IEnumerable<XNode> FindDescendantsNs(string ns, string localName)
        => Descendants().Where(n => n.Namespace == ns && n.Name == localName);

    public bool IsLocal(string localName) => Namespace.Length == 0 && Name == localName;

    public bool IsNs(string ns, string localName) => Namespace == ns && Name == localName;

    public string Get(string name, string defaultValue = null)
        => Attributes.TryGetValue(name, out var value) ? value : defaultValue;

    public string GetNs(string ns, string localName)
        => Attributes.TryGetValue(AttrKey(ns, localName), out var value) ? value : null;

    public bool Has(string name) => Attributes.ContainsKey(name);

    public bool HasNs(string ns, string localName) => Attributes.ContainsKey(AttrKey(ns, localName));

    public XNode Set(string name, string value)
    {
        Attributes[name] = value;
        return this;
    }

    public XNode SetNs(string ns, string localName, string value)
    {
        Attributes[AttrKey(ns, localName)] = value;
        return this;
    }

    public string Pop(string name, string defaultValue = null)
    {
        if (!Attributes.TryGetValue(name, out var value)) return defaultValue;
        Attributes.Remove(name);
        return value;
    }

    public string PopNs(string ns, string localName, string defaultValue = null)
    {
        var key = AttrKey(ns, localName);
        if (!Attributes.TryGetValue(key, out var value)) return defaultValue;
        Attributes.Remove(key);
        return value;
    }

    public void Clear()
    {
        // 對齊 lxml 的 Element.clear()：同時清除子節點、文字與所有屬性。
        foreach (var child in Children) child.Parent = null;
        Children.Clear();
        Attributes.Clear();
        Text = null;
    }

    public XNode Clone()
    {
        var copy = new XNode(Name, Namespace) { Text = Text, Tail = Tail };
        foreach (var pair in Attributes) copy.Attributes[pair.Key] = pair.Value;
        foreach (var pair in NsMap) copy.NsMap[pair.Key] = pair.Value;
        foreach (var child in Children) copy.Append(child.Clone());
        return copy;
    }

    public string IterText()
    {
        var sb = new StringBuilder();
        void Walk(XNode node)
        {
            if (node.Text is not null) sb.Append(node.Text);
            foreach (var child in node.Children)
            {
                Walk(child);
                if (child.Tail is not null) sb.Append(child.Tail);
            }
        }

        Walk(this);
        return sb.ToString();
    }

    public static string AttrKey(string ns, string localName)
        => ns.Length == 0 ? localName : "{" + ns + "}" + localName;

    public static (string Namespace, string LocalName) SplitKey(string key)
    {
        if (key.StartsWith('{'))
        {
            var end = key.IndexOf('}');
            if (end >= 0) return (key[1..end], key[(end + 1)..]);
        }

        return ("", key);
    }

    public static string LocalNameOf(string tag)
        => tag.StartsWith('{') ? tag[(tag.IndexOf('}') + 1)..] : tag;

    public static string NamespaceOf(string tag)
        => tag.StartsWith('{') ? tag[1..tag.IndexOf('}')] : "";
}

public static class XmlNamespaces
{
    public const string XHtml = "http://www.w3.org/1999/xhtml";
    public const string Epub = "http://www.idpf.org/2007/ops";
    public const string Xml = "http://www.w3.org/XML/1998/namespace";
    public const string XLink = "http://www.w3.org/1999/xlink";
    public const string Svg = "http://www.w3.org/2000/svg";
    public const string Opf = "http://www.idpf.org/2007/opf";
    public const string Dc = "http://purl.org/dc/elements/1.1/";
    public const string Ncx = "http://www.daisy.org/z3986/2005/ncx/";
    public const string MathMl = "http://exslt.org/math";
    public const string Idx = "https://kindlegen.s3.amazonaws.com/AmazonKindlePublishingGuidelines.pdf";
}

public static class XhtmlSerializer
{
    public static string Serialize(XNode root, string doctype = "<!DOCTYPE html>", bool xmlDeclaration = true)
    {
        var sb = new StringBuilder();
        if (xmlDeclaration) sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        if (!string.IsNullOrEmpty(doctype)) sb.Append(doctype).Append('\n');
        WriteNode(sb, root, new Dictionary<string, string>(StringComparer.Ordinal), isRoot: true);
        return sb.ToString();
    }

    private static void WriteNode(StringBuilder sb, XNode node, Dictionary<string, string> inheritedPrefixes, bool isRoot = false)
    {
        var prefixes = new Dictionary<string, string>(inheritedPrefixes, StringComparer.Ordinal);
        var localDeclarations = new List<KeyValuePair<string, string>>();
        foreach (var pair in node.NsMap)
        {
            prefixes[pair.Key] = pair.Value;
            localDeclarations.Add(pair);
        }

        sb.Append('<').Append(ElementName(node, prefixes));
        foreach (var declaration in localDeclarations)
        {
            sb.Append(' ');
            if (declaration.Key.Length == 0) sb.Append("xmlns");
            else sb.Append("xmlns:").Append(declaration.Key);
            sb.Append("=\"").Append(EscapeAttribute(declaration.Value)).Append('"');
        }

        var attributes = node.Attributes.ToList();
        if (node.IsLocal("body"))
        {
            attributes = attributes.OrderBy(p =>
            {
                var (ns, local) = XNode.SplitKey(p.Key);
                return ns == XmlNamespaces.Epub && local == "type" ? 0 : 1;
            }).ToList();
        }

        foreach (var pair in attributes)
        {
            var (ns, local) = XNode.SplitKey(pair.Key);
            sb.Append(' ').Append(AttributeName(ns, local, prefixes)).Append("=\"").Append(EscapeAttribute(pair.Value)).Append('"');
        }

        var hasText = !string.IsNullOrEmpty(node.Text);
        if (node.Children.Count == 0 && !hasText)
        {
            sb.Append("/>");
            return;
        }

        sb.Append('>');
        if (hasText) sb.Append(EscapeText(node.Text));
        foreach (var child in node.Children)
        {
            WriteNode(sb, child, prefixes);
            if (!string.IsNullOrEmpty(child.Tail)) sb.Append(EscapeText(child.Tail));
        }

        sb.Append("</").Append(ElementName(node, prefixes)).Append('>');
    }

    private static string ElementName(XNode node, Dictionary<string, string> prefixes)
    {
        if (node.Namespace.Length == 0) return node.Name;
        if (prefixes.TryGetValue("", out var defaultNs) && defaultNs == node.Namespace) return node.Name;

        foreach (var pair in prefixes)
        {
            if (pair.Key.Length > 0 && pair.Value == node.Namespace)
            {
                return pair.Key + ":" + node.Name;
            }
        }

        return node.Name;
    }

    private static string AttributeName(string ns, string local, Dictionary<string, string> prefixes)
    {
        if (ns.Length == 0) return local;
        if (ns == XmlNamespaces.Xml) return "xml:" + local;
        foreach (var pair in prefixes)
        {
            if (pair.Key.Length > 0 && pair.Value == ns) return pair.Key + ":" + local;
        }

        return local;
    }

    public static string EscapeText(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string EscapeAttribute(string value)
        => EscapeText(value).Replace("\"", "&quot;");
}

public static class XhtmlUtil
{
    public static XNode NewElement(string name) => new(name);

    public static XNode NewXhtml()
    {
        var root = new XNode("html", XmlNamespaces.XHtml);
        root.NsMap[""] = XmlNamespaces.XHtml;
        root.NsMap["epub"] = XmlNamespaces.Epub;
        return root;
    }

    public static XNode SubElement(XNode parent, string name)
    {
        var child = new XNode(name);
        parent.Append(child);
        return child;
    }

    public static XNode SubElementNs(XNode parent, string ns, string name)
    {
        var child = new XNode(name, ns);
        parent.Append(child);
        return child;
    }

    public static XNode ReplaceElementWithContainer(XNode elem, string tag)
    {
        var parent = elem.Parent ?? throw new InvalidOperationException("元素沒有父節點");
        var index = parent.Children.IndexOf(elem);
        parent.RemoveChild(elem);
        var container = new XNode(tag);
        container.Append(elem);
        parent.Insert(index, container);
        return container;
    }

    public static void StripTags(XNode parent, string tag)
    {
        for (var i = 0; i < parent.Children.Count; i++)
        {
            var child = parent.Children[i];
            if (child.Namespace.Length == 0 && child.Name == tag)
            {
                parent.Children.RemoveAt(i);
                child.Parent = null;
                var insert = i;
                if (!string.IsNullOrEmpty(child.Text))
                {
                    if (insert == 0)
                    {
                        parent.Text = (parent.Text ?? "") + child.Text;
                    }
                    else
                    {
                        var previous = parent.Children[insert - 1];
                        previous.Tail = (previous.Tail ?? "") + child.Text;
                    }
                }

                foreach (var grandchild in child.Children.ToList())
                {
                    child.RemoveChild(grandchild);
                    parent.Insert(insert++, grandchild);
                }

                if (!string.IsNullOrEmpty(child.Tail))
                {
                    if (insert == 0) parent.Text = (parent.Text ?? "") + child.Tail;
                    else parent.Children[insert - 1].Tail = (parent.Children[insert - 1].Tail ?? "") + child.Tail;
                }

                i = insert - 1;
            }
            else
            {
                StripTags(child, tag);
            }
        }
    }

    public static void SetNsMap(XNode root, Dictionary<string, string> additional)
    {
        foreach (var pair in additional) root.NsMap[pair.Key] = pair.Value;
    }

    public static XNode SetNsMapForNewElement(XNode node, Dictionary<string, string> nsMap)
    {
        foreach (var pair in nsMap) node.NsMap[pair.Key] = pair.Value;
        return node;
    }

    public static XNode RootElement(XNode node)
    {
        while (node.Parent is not null) node = node.Parent;
        return node;
    }
}
