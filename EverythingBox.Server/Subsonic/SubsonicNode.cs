using System.Xml.Linq;

namespace EverythingBox.Server.Subsonic;

/// <summary>A Subsonic response element: a name, ordered string attributes, and child nodes. Rendered
/// to XML (attributes → XML attributes, children → child elements) OR JSON (attributes + single-child
/// groups → object properties; repeated child names → arrays) so the two formats stay identical.
///
/// Values are uniformly string-typed in v1 — numbers and booleans are carried as their string form in
/// BOTH formats. Subsonic clients tolerate string-valued fields, and keeping one representation avoids
/// the per-endpoint typed/untyped drift that creeps in once some fields are emitted as raw JSON numbers
/// and others as strings.</summary>
public sealed class SubsonicNode(string name)
{
    public string Name { get; } = name;
    public List<(string Key, string Value)> Attributes { get; } = [];
    public List<SubsonicNode> Children { get; } = [];
    public string? Text { get; set; }   // rare: element text (e.g. an error message is an attribute, not text)

    public SubsonicNode Attr(string key, string? value) { if (value is not null) Attributes.Add((key, value)); return this; }
    public SubsonicNode Add(SubsonicNode child) { Children.Add(child); return this; }

    /// <summary>Renders the node tree to XML: each attribute becomes an <see cref="XAttribute"/> and each
    /// child a nested element, recursively. Attribute order is preserved.</summary>
    public static XElement ToXml(SubsonicNode node)
    {
        var el = new XElement(node.Name);
        foreach (var (key, value) in node.Attributes)
            el.Add(new XAttribute(key, value));
        if (node.Text is not null)
            el.Add(new XText(node.Text));
        foreach (var child in node.Children)
            el.Add(ToXml(child));
        return el;
    }

    /// <summary>Renders the node tree to the Subsonic-JSON shape. Attributes become object properties;
    /// child nodes are grouped by name — a lone child of a name becomes a nested object, while two or
    /// more children sharing a name collapse into a JSON array under that name (e.g. many
    /// <c>&lt;album&gt;</c> → <c>"album":[ … ]</c>). This array-collapse is the asymmetry that justifies
    /// one model rendered by two renderers rather than two hand-written shapes.</summary>
    public static object ToJson(SubsonicNode node)
    {
        var obj = new Dictionary<string, object?>();
        foreach (var (key, value) in node.Attributes)
            obj[key] = value;
        if (node.Text is not null)
            obj["value"] = node.Text;
        // GroupBy preserves the first-seen order of each distinct child name.
        foreach (var group in node.Children.GroupBy(c => c.Name))
        {
            var items = group.ToList();
            obj[group.Key] = items.Count == 1 ? ToJson(items[0]) : items.Select(ToJson).ToList();
        }
        return obj;
    }
}
