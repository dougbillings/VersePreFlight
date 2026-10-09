using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VersePreflight.Core.IO;

/// <summary>
/// Directory links that need no admin rights: NTFS junctions on Windows,
/// symbolic links elsewhere.
/// </summary>
public static partial class DirectoryLinks
{
    /// <summary>The link's target, or null if <paramref name="path"/> is not a link (or doesn't exist).</summary>
    public static string? GetTarget(string path)
    {
        var target = new DirectoryInfo(path).LinkTarget;
        if (target is null) return null;
        if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = target[4..];
        return Path.IsPathRooted(target)
            ? target
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, target));
    }

    public static bool IsLink(string path) => GetTarget(path) is not null;

    /// <summary>Creates a link at <paramref name="linkPath"/> (which must not exist) pointing to <paramref name="targetPath"/>.</summary>
    public static void Create(string linkPath, string targetPath)
    {
        var target = PathUtil.Normalize(targetPath);
        if (OperatingSystem.IsWindows())
            CreateJunction(linkPath, target);
        else
            Directory.CreateSymbolicLink(linkPath, target);
    }

    /// <summary>Removes only the link itself; the target folder and its contents are untouched.</summary>
    public static void Remove(string linkPath)
    {
        if (!IsLink(linkPath))
            throw new IOException($"'{linkPath}' is not a directory link.");

        if (OperatingSystem.IsWindows())
            Directory.Delete(linkPath, recursive: false); // deletes the junction, never its target
        else
            File.Delete(linkPath); // unlink(2) on the symlink
    }

    // ---- Windows junction (mount point reparse point) ----

    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    [SupportedOSPlatform("windows")]
    private static void CreateJunction(string linkPath, string targetPath)
    {
        if (Path.Exists(linkPath))
            throw new IOException($"Cannot create link: '{linkPath}' already exists.");

        Directory.CreateDirectory(linkPath);
        try
        {
            var buffer = BuildMountPointBuffer(targetPath);
            using var handle = CreateFile(linkPath, GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot open '{linkPath}'.");

            if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot create junction '{linkPath}' -> '{targetPath}'.");
        }
        catch
        {
            Directory.Delete(linkPath, recursive: false);
            throw;
        }
    }

    /// <summary>REPARSE_DATA_BUFFER for a mount point: header, then "\??\target\0" and "target\0".</summary>
    internal static byte[] BuildMountPointBuffer(string targetPath)
    {
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + targetPath);
        var print = Encoding.Unicode.GetBytes(targetPath);
        const int nul = 2; // UTF-16 terminator

        // MountPointReparseBuffer = 4 USHORT fields + PathBuffer
        var dataLength = 8 + substitute.Length + nul + print.Length + nul;
        var buffer = new byte[8 + dataLength]; // ReparseTag + ReparseDataLength + Reserved

        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], IO_REPARSE_TAG_MOUNT_POINT);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], checked((ushort)dataLength));
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);                                   // SubstituteNameOffset
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)substitute.Length);         // SubstituteNameLength
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)(substitute.Length + nul)); // PrintNameOffset
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)print.Length);              // PrintNameLength
        substitute.CopyTo(span[16..]);
        print.CopyTo(span[(16 + substitute.Length + nul)..]);
        return buffer;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, byte[] inBuffer, int inBufferSize,
        IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
