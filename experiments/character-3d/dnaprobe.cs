// Reverse-engineering probe for CIG "DNA V1.6" head files (read-only, scratch only).
using System.Text;
using System.Text.Json;

var work = Path.Combine(Path.GetTempPath(), "claude", "c--Users-dougb-Nextcloud-Star-Citizen-TOOLS", "9bed0ac8-1943-47d2-a8fb-f6e0482a5336", "scratchpad", "chf3d");
var dnaPath = Path.Combine(work, @"extract\Data\Objects\Characters\Human\heads\male\pu\protos_human_male_face_t1_pu\protos_human_male_face_t1_pu_head.dna");
var dna = File.ReadAllBytes(dnaPath);
var verts = ReadGlbPositions(Path.Combine(work, "male_head.glb"));
Console.WriteLine($"DNA bytes: {dna.Length:N0}; GLB vertices: {verts.Length}");
Console.WriteLine($"First GLB vertex: {verts[0].X:F5} {verts[0].Y:F5} {verts[0].Z:F5}");

// Header as u16/u32 fields
Console.WriteLine("Header u16 @0x24..0x60: " + string.Join(" ", Enumerable.Range(0, 30).Select(i => BitConverter.ToUInt16(dna, 0x24 + i * 2))));

// 1) Look for the first vertex as float32 in any axis order (GLB may swap Y/Z)
float[][] orders(System.Numerics.Vector3 v) => [[v.X, v.Y, v.Z], [v.X, -v.Z, v.Y], [v.X, v.Z, -v.Y], [v.X, v.Z, v.Y]];
foreach (var o in orders(verts[0]))
{
    var hits = FindFloatTriplet(dna, o, 1e-4f).Take(5).ToList();
    Console.WriteLine($"float32 triplet ({o[0]:F4},{o[1]:F4},{o[2]:F4}): {(hits.Count == 0 ? "none" : string.Join(", ", hits.Select(h => $"0x{h:X}")))}");
}

// 2) Find runs of plausible float32 values (|v| < 3, not denormal) to map out float arrays
Console.WriteLine("Long float32 runs (>= 3000 floats):");
int runStart = -1, runLen = 0;
for (var i = 0; i + 4 <= dna.Length; i += 4)
{
    var f = BitConverter.ToSingle(dna, i);
    var ok = float.IsFinite(f) && Math.Abs(f) < 3f && (f == 0 || Math.Abs(f) > 1e-7f);
    if (ok) { if (runStart < 0) runStart = i; runLen++; }
    else
    {
        if (runLen >= 3000) Console.WriteLine($"  0x{runStart:X8} len={runLen} floats ({runLen / 3} triplets, /5584={runLen / 3.0 / 5584:F2})");
        runStart = -1; runLen = 0;
    }
}

// 3) Printable strings, to spot section names
var sb = new StringBuilder(); var found = new List<(int, string)>();
for (var i = 0; i < dna.Length; i++)
{
    var c = dna[i];
    if (c is >= 32 and < 127) sb.Append((char)c);
    else { if (sb.Length >= 6) found.Add((i - sb.Length, sb.ToString())); sb.Clear(); }
}
Console.WriteLine($"Strings (>=6 chars): {found.Count}; first 40:");
foreach (var (off, s) in found.Take(40)) Console.WriteLine($"  0x{off:X8} {s}");

static IEnumerable<int> FindFloatTriplet(byte[] data, float[] t, float eps)
{
    for (var i = 0; i + 12 <= data.Length; i += 4)
        if (Math.Abs(BitConverter.ToSingle(data, i) - t[0]) < eps &&
            Math.Abs(BitConverter.ToSingle(data, i + 4) - t[1]) < eps &&
            Math.Abs(BitConverter.ToSingle(data, i + 8) - t[2]) < eps)
            yield return i;
}

static System.Numerics.Vector3[] ReadGlbPositions(string path)
{
    var b = File.ReadAllBytes(path);
    var jsonLen = BitConverter.ToInt32(b, 12);
    using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(b, 20, jsonLen));
    var binStart = 20 + jsonLen + 8;
    var root = doc.RootElement;
    var posAccessor = root.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").GetProperty("POSITION").GetInt32();
    var acc = root.GetProperty("accessors")[posAccessor];
    var view = root.GetProperty("bufferViews")[acc.GetProperty("bufferView").GetInt32()];
    var count = acc.GetProperty("count").GetInt32();
    var offset = binStart + (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0) + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
    var stride = view.TryGetProperty("byteStride", out var st) ? st.GetInt32() : 12;
    return Enumerable.Range(0, count).Select(i => new System.Numerics.Vector3(
        BitConverter.ToSingle(b, offset + i * stride), BitConverter.ToSingle(b, offset + i * stride + 4), BitConverter.ToSingle(b, offset + i * stride + 8))).ToArray();
}
