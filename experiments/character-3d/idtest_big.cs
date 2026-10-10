#:package SkiaSharp@3.119.4
// Renders preset heads for customizer ids 0-34 under two index hypotheses, beside starchar's reference images.
using System.Numerics;
using System.Text;
using System.Text.Json;
using SkiaSharp;

var work = Path.Combine(Path.GetTempPath(), "claude", "c--Users-dougb-Nextcloud-Star-Citizen-TOOLS", "9bed0ac8-1943-47d2-a8fb-f6e0482a5336", "scratchpad", "chf3d");
var dna = File.ReadAllBytes(Path.Combine(work, @"extract\Data\Objects\Characters\Human\heads\male\pu\protos_human_male_face_t1_pu\protos_human_male_face_t1_pu_head.dna"));
// Hypothesis B: slots start at 0x2A8, so slot 0 is the base protos head
var slots = Enumerable.Range(0, 61).Select(i => Encoding.ASCII.GetString(dna, 0x2A8 + i * 40, 40).Split('\0')[0]).ToList();
Console.WriteLine("Slots: " + string.Join(", ", slots.Take(36).Select((n, i) => $"{i}:{n}")));

var (basePos, idx) = ReadGlb(Path.Combine(work, "male_head.glb"));
Vector3[] Load(string name)
{
    var p = Path.Combine(work, "heads", name + "_head.glb");
    if (name.StartsWith("protos")) return basePos;
    if (!File.Exists(p)) return basePos;
    var pos = ReadGlb(p).P;
    var off = Enumerable.Range(0, pos.Length).Aggregate(Vector3.Zero, (a, i) => a + pos[i] - basePos[i]) / pos.Length;
    return pos.Select(v => v - off).ToArray();
}
var center = basePos.Aggregate(Vector3.Zero, (a, b) => a + b) / basePos.Length;

const int Cell = 300; int[] pick = [2, 4, 14, 20, 30]; var Ids = pick.Length;
using var sheet = new SKBitmap(Ids * Cell / 5 * 1, 1); // placeholder, replaced below
var cols = 1; var rowsPer = (Ids + cols - 1) / cols;
using var outBmp = new SKBitmap(cols * Cell * 3, rowsPer * (Cell + 20));
using (var c = new SKCanvas(outBmp))
{
    c.Clear(new SKColor(16, 20, 28));
    using var font = new SKFont(SKTypeface.Default, 17); using var paint = new SKPaint { Color = SKColors.White };
    for (var k = 0; k < Ids; k++)
    {
        var id = pick[k]; float x = 0, y = k * (Cell + 20);
        var refPath = Path.Combine(work, "starchar", $"{id:00}.webp");
        if (File.Exists(refPath)) { using var r = SKBitmap.Decode(refPath); c.DrawBitmap(r, new SKRect(x, y + 20, x + Cell, y + 20 + Cell)); }
        var nameA = id + 1 < slots.Count ? slots[id + 1] : "?"; // hypothesis A: names start at 0x2D0
        var nameB = slots[id];                                   // hypothesis B: names start at 0x2A8
        using var a = Render(Load(nameA), idx, center, Cell); c.DrawBitmap(a, x + Cell, y + 20);
        using var b = Render(Load(nameB), idx, center, Cell); c.DrawBitmap(b, x + 2 * Cell, y + 20);
        c.DrawText($"{id}  A:{Short(nameA)}  B:{Short(nameB)}", x + 4, y + 15, font, paint);
    }
}
using (var fs = File.Create(Path.Combine(work, "id_hypotheses_big.png"))) outBmp.Encode(fs, SKEncodedImageFormat.Png, 90);
Console.WriteLine("Wrote id_hypotheses.png");

static string Short(string n) => n.Replace("male_archetype_", "arch").Replace("_t1", "").Replace("_t2", "").Replace("_t3", "").Replace("protos_human_male_face_pu", "BASE");

// 3/4 view to the character's right, like starchar's thumbnails
static SKBitmap Render(Vector3[] pos, int[] idx, Vector3 center, int size)
{
    var yaw = Matrix4x4.CreateFromAxisAngle(Vector3.UnitZ, -30 * MathF.PI / 180);
    var fwd = Vector3.Transform(Vector3.UnitY, yaw); var up = Vector3.UnitZ; var right = Vector3.Normalize(Vector3.Cross(up, fwd));
    var pts = pos.Select(p => p - center - new Vector3(0, 0, 0.06f)).ToArray();
    float scale = 620f * size / 150f;
    var proj = pts.Select(p => new Vector3(size / 2f + Vector3.Dot(p, right) * scale, size / 2f - Vector3.Dot(p, up) * scale, Vector3.Dot(p, fwd))).ToArray();
    var depth = new float[size * size]; Array.Fill(depth, float.NegativeInfinity);
    var bmp = new SKBitmap(size, size); bmp.Erase(new SKColor(16, 20, 28));
    var light = Vector3.Normalize(fwd * 0.7f + up * 0.5f - right * 0.4f);
    for (var t = 0; t + 2 < idx.Length; t += 3)
    {
        int a = idx[t], b = idx[t + 1], cc = idx[t + 2];
        var n = Vector3.Normalize(Vector3.Cross(pts[b] - pts[a], pts[cc] - pts[a]));
        var g = (byte)(40 + 200 * Math.Clamp(Math.Abs(Vector3.Dot(n, light)), 0, 1));
        var col = new SKColor(g, (byte)(g * 0.95f), (byte)(g * 0.9f));
        var (p0, p1, p2) = (proj[a], proj[b], proj[cc]);
        var area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
        if (Math.Abs(area) < 1e-6f) continue;
        int minX = Math.Max(0, (int)Math.Min(p0.X, Math.Min(p1.X, p2.X))), maxX = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))));
        int minY = Math.Max(0, (int)Math.Min(p0.Y, Math.Min(p1.Y, p2.Y))), maxY = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))));
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            var w0 = ((p1.X - px) * (p2.Y - py) - (p2.X - px) * (p1.Y - py)) / area;
            var w1 = ((p2.X - px) * (p0.Y - py) - (p0.X - px) * (p2.Y - py)) / area;
            var w2 = 1 - w0 - w1;
            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
            var z = w0 * p0.Z + w1 * p1.Z + w2 * p2.Z;
            if (z <= depth[y * size + x]) continue;
            depth[y * size + x] = z; bmp.SetPixel(x, y, col);
        }
    }
    return bmp;
}

static (Vector3[] P, int[] I) ReadGlb(string path)
{
    var b = File.ReadAllBytes(path);
    var jsonLen = BitConverter.ToInt32(b, 12);
    using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(b, 20, jsonLen));
    var bin = 20 + jsonLen + 8; var root = doc.RootElement;
    var prim = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
    (int Off, int Count, int Stride, int Type) Acc(int i, int def)
    {
        var a = root.GetProperty("accessors")[i]; var v = root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
        return (bin + (v.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0) + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0),
            a.GetProperty("count").GetInt32(), v.TryGetProperty("byteStride", out var s) ? s.GetInt32() : def, a.GetProperty("componentType").GetInt32());
    }
    var pa = Acc(prim.GetProperty("attributes").GetProperty("POSITION").GetInt32(), 12);
    var p = Enumerable.Range(0, pa.Count).Select(i => new Vector3(BitConverter.ToSingle(b, pa.Off + i * pa.Stride), BitConverter.ToSingle(b, pa.Off + i * pa.Stride + 4), BitConverter.ToSingle(b, pa.Off + i * pa.Stride + 8))).ToArray();
    var ia = Acc(prim.GetProperty("indices").GetInt32(), 0);
    var ix = Enumerable.Range(0, ia.Count).Select(i => ia.Type == 5125 ? (int)BitConverter.ToUInt32(b, ia.Off + i * 4) : BitConverter.ToUInt16(b, ia.Off + i * 2)).ToArray();
    return (p, ix);
}
