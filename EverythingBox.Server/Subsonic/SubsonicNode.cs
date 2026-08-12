using System.Globalization;
using System.Xml.Linq;

namespace EverythingBox.Server.Subsonic;

/// <summary>The value kind an attribute carries into the JSON renderer. XML renders every kind as a
/// string (the XML wire is unchanged); JSON renders <see cref="Number"/> as a raw JSON number and
/// <see cref="Bool"/> as a raw boolean, while <see cref="String"/> stays quoted.</summary>
public enum SubsonicAttrKind { String, Number, Bool }

/// <summary>A Subsonic response element: a name, ordered attributes (each with a value KIND), and child
/// nodes. Rendered to XML (attributes → XML attributes, children → child elements) OR JSON (attributes +
/// single-child groups → object properties; repeated child names → arrays) so the two formats stay
/// structurally identical.
///
/// XML carries every value as a string (the historic wire). JSON is typed: real Subsonic/OpenSubsonic
/// clients (and strict JSON parsers) expect native numbers for counts/duration/track/discNumber/year/size
/// and native booleans for valid/isDir — so a field built with <see cref="AttrNum"/>/<see cref="AttrBool"/>
/// renders unquoted in JSON. Ids and names stay <see cref="Attr"/> (string) even when all-digit, because an
/// id must remain a JSON string.</summary>
public sealed class SubsonicNode(string name)
{
    public string Name { get; } = name;
    public List<(string Key, string Value, SubsonicAttrKind Kind)> Attributes { get; } = [];
    public List<SubsonicNode> Children { get; } = [];
    public string? Text { get; set; }   // rare: element text (e.g. an error message is an attribute, not text)

    /// <summary>A string-valued attribute (ids, names, suffixes, …). Dropped when null.</summary>
    public SubsonicNode Attr(string key, string? value)
    { if (value is not null) Attributes.Add((key, value, SubsonicAttrKind.String)); return this; }

    /// <summary>A numeric attribute — a raw JSON number, a string in XML. Dropped when null.</summary>
    public SubsonicNode AttrNum(string key, long? value)
    { if (value is not null) Attributes.Add((key, value.Value.ToString(CultureInfo.InvariantCulture), SubsonicAttrKind.Number)); return this; }

    /// <summary>A boolean attribute — a raw JSON boolean, "true"/"false" in XML. Dropped when null.</summary>
    public SubsonicNode AttrBool(string key, bool? value)
    { if (value is not null) Attributes.Add((key, value.Value ? "true" : "false", SubsonicAttrKind.Bool)); return this; }

    public SubsonicNode Add(SubsonicNode child) { Children.Add(child); return this; }

    /// <summary>Renders the node tree to XML: each attribute becomes an <see cref="XAttribute"/> (always its
    /// string form, regardless of kind) and each child a nested element, recursively. Order is preserved.</summary>
    public static XElement ToXml(SubsonicNode node)
    {
        var el = new XElement(node.Name);
        foreach (var (key, value, _) in node.Attributes)
            el.Add(new XAttribute(key, value));
        if (node.Text is not null)
            el.Add(new XText(node.Text));
        foreach (var child in node.Children)
            el.Add(ToXml(child));
        return el;
    }

    /// <summary>Renders the node tree to the Subsonic-JSON shape. Attributes become object properties —
    /// typed by their kind, so a Number is a boxed <see cref="long"/> and a Bool a boxed <see cref="bool"/>
    /// (System.Text.Json serialises each by its runtime type, emitting unquoted values). Child nodes are
    /// grouped by name — a lone child becomes a nested object, two or more sharing a name collapse into a
    /// JSON array (e.g. many <c>&lt;album&gt;</c> → <c>"album":[ … ]</c>).</summary>
    public static object ToJson(SubsonicNode node)
    {
        var obj = new Dictionary<string, object?>();
        foreach (var (key, value, kind) in node.Attributes)
            obj[key] = kind switch
            {
                SubsonicAttrKind.Number => long.Parse(value, CultureInfo.InvariantCulture),
                SubsonicAttrKind.Bool => value == "true",
                _ => value,
            };
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
