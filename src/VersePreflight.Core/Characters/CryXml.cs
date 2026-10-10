using System.Text;

namespace VersePreflight.Core.Characters;

/// <summary>An element of a CryEngine binary XML document.</summary>
public sealed class CryXmlElement(string tag, IReadOnlyDictionary<string, string> attributes)
{
    public string Tag { get; } = tag;
    public IReadOnlyDictionary<string, string> Attributes { get; } = attributes;
    public List<CryXmlElement> Children { get; } = [];

    public string? this[string attribute] => Attributes.GetValueOrDefault(attribute);

    public IEnumerable<CryXmlElement> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));
}

/// <summary>
/// Reads CryEngine binary XML ("CryXmlB"), used by .mtl material files. Layout from StarBreaker's
/// starbreaker-cryxml (MIT, diogotr7): magic, header, node table, child index table, attribute table, strings.
/// </summary>
public static class CryXml
{
    private static readonly byte[] Magic = "CryXmlB\0"u8.ToArray();

    public static bool IsBinary(ReadOnlySpan<byte> data) => data.Length > 8 && data[..8].SequenceEqual(Magic);

    public static CryXmlElement Parse(byte[] d)
    {
        if (!IsBinary(d)) throw new InvalidDataException("Not a CryXmlB document.");
        var h = 8;
        int nodeCount = BitConverter.ToInt32(d, h + 8), attributeCount = BitConverter.ToInt32(d, h + 16);
        int childCount = BitConverter.ToInt32(d, h + 24), stringSize = BitConverter.ToInt32(d, h + 32);

        var nodesAt = h + 36;
        var childrenAt = nodesAt + nodeCount * 28;
        var attributesAt = childrenAt + childCount * 4;
        var stringsAt = attributesAt + attributeCount * 8;
        if (stringsAt + stringSize > d.Length) throw new InvalidDataException("Damaged CryXmlB document.");

        string Str(int offset)
        {
            var start = stringsAt + offset;
            var end = Array.IndexOf(d, (byte)0, start, stringSize - offset);
            return Encoding.UTF8.GetString(d, start, (end < 0 ? stringsAt + stringSize : end) - start);
        }

        var elements = new CryXmlElement[nodeCount];
        for (var n = 0; n < nodeCount; n++)
        {
            var at = nodesAt + n * 28;
            // tag(4) type(4) attribute count(2) child count(2) parent(4) first attribute(4) first child(4) reserved(4)
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var count = BitConverter.ToUInt16(d, at + 8);
            var first = BitConverter.ToInt32(d, at + 16);
            for (var a = 0; a < count; a++)
            {
                var aAt = attributesAt + (first + a) * 8;
                attrs[Str(BitConverter.ToInt32(d, aAt))] = Str(BitConverter.ToInt32(d, aAt + 4));
            }
            elements[n] = new CryXmlElement(Str(BitConverter.ToInt32(d, at)), attrs);
        }
        for (var n = 0; n < nodeCount; n++)
        {
            var at = nodesAt + n * 28;
            var children = BitConverter.ToUInt16(d, at + 10);
            var firstChild = BitConverter.ToInt32(d, at + 20);
            for (var c = 0; c < children; c++)
                elements[n].Children.Add(elements[BitConverter.ToInt32(d, childrenAt + (firstChild + c) * 4)]);
        }
        return elements[0];
    }
}
