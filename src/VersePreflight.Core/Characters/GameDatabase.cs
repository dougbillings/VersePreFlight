using System.Text;

namespace VersePreflight.Core.Characters;

/// <summary>
/// Minimal reader for the game's DataCore database (<c>Game2.dcb</c>): only the record table and
/// string tables, to look up a record's name from its id. Layout from StarBreaker's
/// starbreaker-datacore (MIT, diogotr7).
/// </summary>
public static class GameDatabase
{
    private const int HeaderSize = 120;

    /// <summary>
    /// Returns record names (e.g. "hair_24", without the "EntityClassDefinition." prefix) by record id,
    /// for records whose file path passes <paramref name="fileFilter"/>.
    /// </summary>
    public static Dictionary<Guid, string> ReadRecordNames(byte[] d, Func<string, bool> fileFilter)
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

        string Str(int table, int length, int offset)
        {
            var end = Array.IndexOf(d, (byte)0, table + offset, length - offset);
            return Encoding.UTF8.GetString(d, table + offset, (end < 0 ? table + length : end) - (table + offset));
        }

        var result = new Dictionary<Guid, string>();
        for (var r = 0; r < records; r++)
        {
            var at = (int)(recordsAt + r * (long)recordSize);
            var fileName = Str(table1, text1, BitConverter.ToInt32(d, at + 4));
            if (!fileFilter(fileName)) continue;
            var name = Str(table2, text2, BitConverter.ToInt32(d, at));
            var guidAt = at + (version >= 8 ? 16 : 12); // v8 inserts a tag offset before the struct index
            var id = new Guid(d.AsSpan(guidAt, 16));
            var dot = name.LastIndexOf('.');
            result[id] = dot >= 0 ? name[(dot + 1)..] : name;
        }
        return result;
    }
}
