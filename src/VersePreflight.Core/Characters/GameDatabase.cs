using System.Text;

namespace VersePreflight.Core.Characters;

/// <summary>
/// Minimal reader for the game's DataCore database (<c>Game2.dcb</c>): the record table and string
/// tables, to look up record names by id, plus a narrow lookup of material entries. Layout from
/// StarBreaker's starbreaker-datacore (MIT, diogotr7).
/// </summary>
public static class GameDatabase
{
    private const int HeaderSize = 120;

    private sealed record Layout(int Version, long RecordsAt, int RecordCount, int RecordSize, int Table1, int Text1, int Table2, int Text2)
    {
        public int InstanceData => Table2 + Text2;
    }

    private static Layout ReadLayout(byte[] d)
    {
        var version = BitConverter.ToInt32(d, 4);
        if (version is not (6 or 8)) throw new NotSupportedException($"Game database version {version} isn't supported.");
        int C(int field) => BitConverter.ToInt32(d, 16 + field * 4); // counts after magic, version, 2 reserved
        int structs = C(0), properties = C(1), enums = C(2), mappings = C(3), records = C(4);
        int bools = C(5), i8 = C(6), i16 = C(7), i32 = C(8), i64 = C(9), u8 = C(10), u16 = C(11), u32 = C(12), u64 = C(13);
        int singles = C(14), doubles = C(15), guids = C(16), stringIds = C(17), locales = C(18), enumValues = C(19);
        int strongs = C(20), weaks = C(21), references = C(22), enumOptions = C(23);
        var text1 = BitConverter.ToInt32(d, 112);
        var text2 = BitConverter.ToInt32(d, 116);

        long p = HeaderSize + structs * 16L + properties * 12L + enums * 8L + mappings * 8L;
        var recordSize = version >= 8 ? 36 : 32;
        var recordsAt = p;
        p += records * (long)recordSize;
        p += i8 + i16 * 2L + i32 * 4L + i64 * 8L + u8 + u16 * 2L + u32 * 4L + u64 * 8L + bools + singles * 4L + doubles * 8L;
        p += guids * 16L + stringIds * 4L + locales * 4L + enumValues * 4L + strongs * 8L + weaks * 8L + references * 20L + enumOptions * 4L;
        var table1 = (int)p;
        var table2 = (int)(p + text1);
        if (table2 + text2 > d.Length) throw new InvalidDataException("Damaged game database.");
        return new Layout(version, recordsAt, records, recordSize, table1, text1, table2, text2);
    }

    private static string Str(byte[] d, int table, int length, int offset)
    {
        var end = Array.IndexOf(d, (byte)0, table + offset, length - offset);
        return Encoding.UTF8.GetString(d, table + offset, (end < 0 ? table + length : end) - (table + offset));
    }

    /// <summary>
    /// Returns record names (e.g. "hair_24", without the "EntityClassDefinition." prefix) by record id,
    /// for records whose file path passes <paramref name="fileFilter"/>.
    /// </summary>
    public static Dictionary<Guid, string> ReadRecordNames(byte[] d, Func<string, bool> fileFilter)
    {
        var l = ReadLayout(d);
        var result = new Dictionary<Guid, string>();
        for (var r = 0; r < l.RecordCount; r++)
        {
            var at = (int)(l.RecordsAt + r * (long)l.RecordSize);
            if (!fileFilter(Str(d, l.Table1, l.Text1, BitConverter.ToInt32(d, at + 4)))) continue;
            var name = Str(d, l.Table2, l.Text2, BitConverter.ToInt32(d, at));
            var guidAt = at + (l.Version >= 8 ? 16 : 12); // v8 inserts a tag offset before the struct index
            var dot = name.LastIndexOf('.');
            result[new Guid(d.AsSpan(guidAt, 16))] = dot >= 0 ? name[(dot + 1)..] : name;
        }
        return result;
    }

    /// <summary>
    /// Finds material entries (an id followed by a file path) whose path ends with <paramref name="pathSuffix"/>,
    /// e.g. the character customizer's skin materials. Returns path by id.
    /// This scans instance data for references to matching strings instead of decoding every struct; any
    /// stray match only adds an id that no character uses.
    /// </summary>
    public static Dictionary<Guid, string> FindMaterialPaths(byte[] d, string pathSuffix)
    {
        var l = ReadLayout(d);
        var result = new Dictionary<Guid, string>();
        var instances = d.AsSpan(l.InstanceData);
        Span<byte> needle = stackalloc byte[4];

        for (var offset = 0; offset < l.Text1;)
        {
            var end = Array.IndexOf(d, (byte)0, l.Table1 + offset, l.Text1 - offset);
            if (end < 0) break;
            var length = end - (l.Table1 + offset);
            if (length >= pathSuffix.Length &&
                Encoding.UTF8.GetString(d, end - pathSuffix.Length, pathSuffix.Length).Equals(pathSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var path = Encoding.UTF8.GetString(d, l.Table1 + offset, length);
                BitConverter.TryWriteBytes(needle, offset);
                for (var pos = 0; ;)
                {
                    var i = instances[pos..].IndexOf(needle);
                    if (i < 0) break;
                    pos += i;
                    if (pos >= 16) result.TryAdd(new Guid(instances.Slice(pos - 16, 16)), path); // id stored just before the path
                    pos += 4;
                }
            }
            offset = end - l.Table1 + 1;
        }
        return result;
    }
}
