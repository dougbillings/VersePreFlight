using System.IO.Compression;
using System.Text;
using ZstdSharp;

namespace VersePreflight.Core.Characters;

/// <summary>An entry in the game's Data.p4k archive.</summary>
public sealed record GameArchiveEntry(string Path, long Offset, long CompressedSize, long UncompressedSize, ushort Method, bool IsEncrypted);

/// <summary>
/// Read-only access to Star Citizen's Data.p4k (a ZIP64 archive with CIG extra fields and
/// Zstandard compression). Ported from StarBreaker's starbreaker-p4k (MIT, diogotr7).
///
/// Encrypted entries are deliberately not supported: Verse Preflight only reads unencrypted
/// game data from the player's own install and contains no decryption keys.
/// </summary>
public sealed class GameArchive : IDisposable
{
    private const uint EocdSignature = 0x06054B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const uint Eocd64Signature = 0x06064B50;
    private const uint CentralDirSignature = 0x02014B50;
    private const uint LocalFileSignature = 0x04034B50;
    private const uint LocalFileCigSignature = 0x14034B50;

    private readonly FileStream _stream;
    private readonly Lock _lock = new();

    private GameArchive(FileStream stream) => _stream = stream;

    public static GameArchive Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess));

    /// <summary>
    /// Streams the central directory and returns the entries whose path passes <paramref name="filter"/>.
    /// Paths use backslashes, e.g. <c>Data\Objects\Characters\...</c>.
    /// </summary>
    public List<GameArchiveEntry> FindEntries(Func<string, bool> filter)
    {
        var (count, cdOffset, cdSize, zip64) = LocateCentralDirectory();
        var results = new List<GameArchiveEntry>();

        // The directory can be hundreds of MB; read it through a buffered stream instead of all at once
        using var cd = new BufferedStream(new SubStream(_stream, cdOffset, cdSize, _lock), 1 << 20);
        using var r = new BinaryReader(cd, Encoding.UTF8, leaveOpen: true);
        var nameBuffer = new byte[1024];
        for (long i = 0; i < count; i++)
        {
            if (r.ReadUInt32() != CentralDirSignature) throw new InvalidDataException("Damaged archive directory.");
            r.ReadUInt16(); r.ReadUInt16(); // version made by / needed
            var flags = r.ReadUInt16();
            var method = r.ReadUInt16();
            r.ReadUInt32(); r.ReadUInt32(); // time, crc
            long compressed = r.ReadUInt32(), uncompressed = r.ReadUInt32();
            int nameLen = r.ReadUInt16(), extraLen = r.ReadUInt16(), commentLen = r.ReadUInt16();
            var disk = r.ReadUInt16();
            r.ReadUInt16(); r.ReadUInt32(); // attributes
            long offset = r.ReadUInt32();

            if (nameLen > nameBuffer.Length) nameBuffer = new byte[nameLen];
            r.BaseStream.ReadExactly(nameBuffer, 0, nameLen);
            var extra = r.ReadBytes(extraLen);
            if (commentLen > 0) r.ReadBytes(commentLen);

            var encrypted = (flags & 1) != 0;
            if (zip64) ParseExtra(extra, ref compressed, ref uncompressed, ref offset, disk, ref encrypted);

            var path = Encoding.UTF8.GetString(nameBuffer, 0, nameLen).Replace('/', '\\');
            if (filter(path)) results.Add(new GameArchiveEntry(path, offset, compressed, uncompressed, method, encrypted));
        }
        return results;
    }

    public byte[] Read(GameArchiveEntry entry)
    {
        if (entry.IsEncrypted)
            throw new NotSupportedException($"'{entry.Path}' is encrypted; Verse Preflight only reads unencrypted game data.");

        byte[] raw;
        lock (_lock)
        {
            _stream.Seek(entry.Offset, SeekOrigin.Begin);
            Span<byte> header = stackalloc byte[30];
            _stream.ReadExactly(header);
            var sig = BitConverter.ToUInt32(header);
            if (sig != LocalFileSignature && sig != LocalFileCigSignature) throw new InvalidDataException($"Bad entry header for '{entry.Path}'.");
            var skip = BitConverter.ToUInt16(header[26..]) + BitConverter.ToUInt16(header[28..]);
            _stream.Seek(skip, SeekOrigin.Current);
            raw = new byte[entry.CompressedSize];
            _stream.ReadExactly(raw);
        }

        return entry.Method switch
        {
            0 => raw,
            100 => new Decompressor().Unwrap(raw).ToArray(),
            8 => Inflate(raw, entry.UncompressedSize),
            _ => throw new NotSupportedException($"Unsupported compression method {entry.Method} for '{entry.Path}'."),
        };
    }

    private static byte[] Inflate(byte[] raw, long size)
    {
        using var input = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress);
        var output = new byte[size];
        input.ReadExactly(output);
        return output;
    }

    // ZIP64 extras in CIG's fixed order: 0x0001 (sizes/offset), 0x5000, 0x5002 (encryption), 0x5003
    private static void ParseExtra(byte[] extra, ref long compressed, ref long uncompressed, ref long offset, ushort disk, ref bool encrypted)
    {
        var x = 0;
        while (x + 4 <= extra.Length)
        {
            var tag = BitConverter.ToUInt16(extra, x);
            var size = BitConverter.ToUInt16(extra, x + 2);
            if (tag == 0x0001)
            {
                var p = x + 4;
                if (uncompressed == 0xFFFFFFFF) { uncompressed = (long)BitConverter.ToUInt64(extra, p); p += 8; }
                if (compressed == 0xFFFFFFFF) { compressed = (long)BitConverter.ToUInt64(extra, p); p += 8; }
                if (offset == 0xFFFFFFFF) { offset = (long)BitConverter.ToUInt64(extra, p); }
                _ = disk;
                x += 4 + size;
            }
            else
            {
                // CIG's own fields (0x5000, 0x5002, 0x5003) count their 4-byte tag/size header in
                // the size; 0x5002 (size 6) carries a u16 encryption flag
                if (tag == 0x5002) encrypted = BitConverter.ToUInt16(extra, x + 4) == 1;
                x += Math.Max((int)size, 4);
            }
        }
    }

    private (long Count, long Offset, long Size, bool Zip64) LocateCentralDirectory()
    {
        lock (_lock)
        {
            var tailLen = (int)Math.Min(_stream.Length, 1 << 16);
            var tail = new byte[tailLen];
            _stream.Seek(-tailLen, SeekOrigin.End);
            _stream.ReadExactly(tail);

            var eocd = -1;
            for (var i = tailLen - 22; i >= 0; i--)
                if (BitConverter.ToUInt32(tail, i) == EocdSignature) { eocd = i; break; }
            if (eocd < 0) throw new InvalidDataException("Not a game archive (no end-of-directory record).");

            var entries16 = BitConverter.ToUInt16(tail, eocd + 10);
            var size32 = BitConverter.ToUInt32(tail, eocd + 12);
            var offset32 = BitConverter.ToUInt32(tail, eocd + 16);
            var zip64 = entries16 == 0xFFFF || size32 == 0xFFFFFFFF || offset32 == 0xFFFFFFFF;
            if (!zip64) return (entries16, offset32, size32, false);

            var locator = eocd - 20;
            if (locator < 0 || BitConverter.ToUInt32(tail, locator) != Zip64LocatorSignature)
                throw new InvalidDataException("Damaged archive (no ZIP64 locator).");
            var eocd64 = (long)BitConverter.ToUInt64(tail, locator + 8);
            var record = new byte[56];
            _stream.Seek(eocd64, SeekOrigin.Begin);
            _stream.ReadExactly(record);
            if (BitConverter.ToUInt32(record, 0) != Eocd64Signature) throw new InvalidDataException("Damaged archive (bad ZIP64 record).");
            return ((long)BitConverter.ToUInt64(record, 32), (long)BitConverter.ToUInt64(record, 48), (long)BitConverter.ToUInt64(record, 40), true);
        }
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>A read-only window onto part of the archive file, sharing the file handle under a lock.</summary>
    private sealed class SubStream(FileStream inner, long start, long length, Lock sync) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            if (n <= 0) return 0;
            lock (sync)
            {
                inner.Seek(start + _position, SeekOrigin.Begin);
                n = inner.Read(buffer, offset, n);
            }
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
