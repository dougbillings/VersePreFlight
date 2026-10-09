using System.Security.Cryptography;

namespace VersePreflight.Core.IO;

public static class FileCompare
{
    public static bool AreIdentical(string pathA, string pathB)
    {
        var a = new FileInfo(pathA);
        var b = new FileInfo(pathB);
        if (!a.Exists || !b.Exists || a.Length != b.Length) return false;
        return HashFile(pathA).AsSpan().SequenceEqual(HashFile(pathB));
    }

    public static byte[] HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }
}
