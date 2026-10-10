#:package SkiaSharp@3.119.4
// Prototype: blend preset heads per face region using a .chf's DNA, then software-render grey previews.
using System.Numerics;
using System.Text;
using System.Text.Json;
using SkiaSharp;

var work = Path.Combine(Path.GetTempPath(), "claude", "c--Users-dougb-Nextcloud-Star-Citizen-TOOLS", "9bed0ac8-1943-47d2-a8fb-f6e0482a5336", "scratchpad", "chf3d");
var chfName = args.Length > 0 ? args[0] : "gomab";
var headDir = Path.Combine(work, @"extract\Data\Objects\Characters\Human\heads\male\pu\protos_human_male_face_t1_pu");
var dna = File.ReadAllBytes(Path.Combine(headDir, "protos_human_male_face_t1_pu_head.dna"));
const int Verts = 5584, Regions = 13, MaskStart = 0x14F5B78;

// --- Inputs ---
var names = Enumerable.Range(0, 60).Select(i => Encoding.ASCII.GetString(dna, 0x2D0 + i * 40, 40).TrimEnd('\0')).ToList();
var (basePos, indices) = ReadGlb(Path.Combine(work, "male_head.glb"));
var heads = names.Select(n => { var p = Path.Combine(work, "heads", n + "_head.glb"); return File.Exists(p) ? ReadGlb(p).Positions : null; }).ToList();
Console.WriteLine($"Loaded base + {heads.Count(h => h is not null)}/60 preset heads");

// Some presets are stored with a global offset; remove each head's mean offset from the base
var alignHeads = !args.Contains("--no-align");
if (alignHeads)
    for (var h = 0; h < heads.Count; h++)
    {
        if (heads[h] is not { } hp) continue;
        var offset = Enumerable.Range(0, Verts).Aggregate(Vector3.Zero, (acc, i) => acc + (hp[i] - basePos[i])) / Verts;
        if (offset.Length() > 0.005f) Console.WriteLine($"  aligning {names[h]}: offset {offset * 1000} mm");
        heads[h] = hp.Select(p => p - offset).ToArray();
    }

using var remapDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(headDir, "protos_human_male_face_t1_pu_vertexOrderRemapTables.json")));
var preRc = remapDoc.RootElement.GetProperty("vertexOrderRemapTables").EnumerateArray()
    .First(t => t.GetProperty("meshName").GetString() == "head" && t.GetProperty("lodName").GetString() == "lod0")
    .GetProperty("preRCIndices").EnumerateArray().Select(e => e.GetInt32()).ToArray();
Console.WriteLine($"Remap table: {preRc.Length} entries");
var inverse = new int[Verts]; for (var i = 0; i < preRc.Length; i++) inverse[preRc[i]] = i;

float MaskRaw(int r, int dnaVertex) => BitConverter.ToSingle(dna, MaskStart + 4 * (r * Verts + dnaVertex));

// Pick the remap direction under which regions are spatially compact
double Spread(Func<int, int> meshToDna)
{
    double total = 0;
    for (var r = 0; r < Regions; r++)
    {
        var pts = Enumerable.Range(0, Verts).Where(i => MaskRaw(r, meshToDna(i)) > 0.5f).Select(i => basePos[i]).ToList();
        if (pts.Count == 0) continue;
        var c = pts.Aggregate(Vector3.Zero, (a, b) => a + b) / pts.Count;
        total += pts.Average(p => (p - c).Length());
    }
    return total;
}
var candidates = new (string Name, Func<int, int> Map)[] { ("identity", i => i), ("preRC[i]", i => preRc[i]), ("inverse", i => inverse[i]) };
foreach (var c in candidates) Console.WriteLine($"  region spread with {c.Name}: {Spread(c.Map):F3}");
var forced = args.FirstOrDefault(a => a.StartsWith("--map="))?[6..];
var best = forced is null ? candidates.MinBy(c => Spread(c.Map)) : candidates.First(c => c.Name == forced);
Console.WriteLine($"Using vertex mapping: {best.Name}");

// Diagnostic sheet: base head coloured by strongest region, once per mapping
SKColor[] palette = [SKColors.Red, SKColors.OrangeRed, SKColors.Gold, SKColors.Yellow, SKColors.Lime, SKColors.Cyan, SKColors.DeepSkyBlue,
    SKColors.Blue, SKColors.Purple, SKColors.Magenta, SKColors.Pink, SKColors.White, SKColors.Gray];
{
    var fwd0 = Vector3.UnitY; var c0 = Enumerable.Range(0, Verts).Aggregate(Vector3.Zero, (a, i) => a + basePos[i]) / Verts;
    const int S = 420;
    using var maskSheet = new SKBitmap(S * candidates.Length, S);
    using (var cv = new SKCanvas(maskSheet))
    {
        cv.Clear(SKColors.Black);
        for (var k = 0; k < candidates.Length; k++)
        {
            var map = candidates[k].Map;
            var colors = Enumerable.Range(0, Verts).Select(i => palette[Enumerable.Range(0, Regions).MaxBy(r => MaskRaw(r, map(i)))]).ToArray();
            using var img = Render(basePos, indices, fwd0, Vector3.UnitZ, c0, 20, S, colors);
            cv.DrawBitmap(img, k * S, 0);
            using var f = new SKFont(SKTypeface.Default, 18); using var pt = new SKPaint { Color = SKColors.White };
            cv.DrawText(candidates[k].Name, k * S + 10, 22, f, pt);
        }
    }
    using var fs0 = File.Create(Path.Combine(work, "mask_mappings.png")); maskSheet.Encode(fs0, SKEncodedImageFormat.Png, 100);
}
float Mask(int r, int meshVertex) => MaskRaw(r, best.Map(meshVertex));

// --- The character's DNA (decoded by StarBreaker to JSON) ---
using var chf = JsonDocument.Parse(File.ReadAllText(Path.Combine(work, chfName + ".json")));
string[] partNames = ["EyebrowLeft", "EyebrowRight", "EyeLeft", "EyeRight", "Nose", "EarLeft", "EarRight", "CheekLeft", "CheekRight", "Mouth", "Jaw", "Crown", "Neck"];
var parts = chf.RootElement.GetProperty("dna").GetProperty("face_parts");
var recipe = new (int Head, float Weight)[Regions][];
for (var r = 0; r < Regions; r++)
    recipe[r] = parts.TryGetProperty(partNames[r], out var blends)
        ? blends.EnumerateArray().Select(b => (b.GetProperty("head_id").GetInt32(), b.GetProperty("value").GetInt32() / 65535f)).Where(b => b.Item2 > 0).ToArray()
        : [];

// --- Blend: per vertex, sum over regions of mask x (weighted preset positions); regions without a recipe keep the base ---
var blended = new Vector3[Verts];
for (var i = 0; i < Verts; i++)
{
    var p = Vector3.Zero;
    for (var r = 0; r < Regions; r++)
    {
        var m = Mask(r, i);
        if (m == 0) continue;
        var regionPos = Vector3.Zero; float wsum = 0;
        foreach (var (h, w) in recipe[r])
        {
            var src = heads[h] ?? basePos;
            regionPos += w * src[i]; wsum += w;
        }
        p += m * (wsum > 0 ? regionPos / wsum : basePos[i]);
    }
    blended[i] = p;
}
var maxMove = Enumerable.Range(0, Verts).Max(i => (blended[i] - basePos[i]).Length());
Console.WriteLine($"Blended {chfName}: max vertex offset from base {maxMove * 1000:F1} mm");

// --- Render sheet ---
var up = Vector3.UnitZ; // CryEngine is Z-up; heights ~1.7 m
var noseCentroid = Centroid(basePos, i => Mask(4, i) > 0.5f);
var headCentroid = Centroid(basePos, _ => true);
var forward = Vector3.Normalize(new Vector3(noseCentroid.X - headCentroid.X, noseCentroid.Y - headCentroid.Y, 0));
Console.WriteLine($"Face forward axis: {forward}");

var panels = new (string Label, Vector3[] Pos, float Yaw)[]
{
    ("base head", basePos, 0), ($"preset 20 ({names[20]})", heads[20] ?? basePos, 0),
    ($"{chfName} (blended)", blended, 0), ($"{chfName} 3/4", blended, 35),
};
const int Size = 480;
using var sheet = new SKBitmap(Size * panels.Length, Size + 34);
using (var canvas = new SKCanvas(sheet))
{
    canvas.Clear(new SKColor(0x10, 0x14, 0x1C));
    using var font = new SKFont(SKTypeface.Default, 18);
    using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
    for (var k = 0; k < panels.Length; k++)
    {
        using var img = Render(panels[k].Pos, indices, forward, up, headCentroid, panels[k].Yaw, Size);
        canvas.DrawBitmap(img, k * Size, 34);
        canvas.DrawText(panels[k].Label, k * Size + 12, 24, font, paint);
    }
}
var outPath = Path.Combine(work, $"render_{chfName}.png");
using (var fs = File.Create(outPath)) sheet.Encode(fs, SKEncodedImageFormat.Png, 100);
Console.WriteLine($"Wrote {outPath}");

static Vector3 Centroid(Vector3[] pos, Func<int, bool> include)
{
    var pts = Enumerable.Range(0, pos.Length).Where(include).Select(i => pos[i]).ToList();
    return pts.Aggregate(Vector3.Zero, (a, b) => a + b) / pts.Count;
}

// Minimal z-buffered software rasterizer with Lambert shading (grey clay look)
static SKBitmap Render(Vector3[] pos, int[] idx, Vector3 forward, Vector3 up, Vector3 center, float yawDeg, int size, SKColor[]? vertexColors = null)
{
    var yaw = Matrix4x4.CreateFromAxisAngle(up, yawDeg * MathF.PI / 180);
    var fwd = Vector3.Transform(forward, yaw);
    var right = Vector3.Normalize(Vector3.Cross(up, fwd));
    var pts = pos.Select(p => p - center).ToArray();
    const float scale = 1350f; // pixels per metre
    var proj = pts.Select(p => new Vector3(size / 2f + Vector3.Dot(p, right) * scale, size / 2f - Vector3.Dot(p, up) * scale - 10, Vector3.Dot(p, fwd))).ToArray();
    var depth = new float[size * size]; Array.Fill(depth, float.NegativeInfinity);
    var bmp = new SKBitmap(size, size);
    var bg = new SKColor(0x10, 0x14, 0x1C);
    for (var i = 0; i < size * size; i++) bmp.SetPixel(i % size, i / size, bg);
    var light = Vector3.Normalize(fwd * 0.8f + up * 0.5f + right * 0.3f);
    for (var t = 0; t + 2 < idx.Length; t += 3)
    {
        int a = idx[t], b = idx[t + 1], c = idx[t + 2];
        var n = Vector3.Normalize(Vector3.Cross(pts[b] - pts[a], pts[c] - pts[a]));
        var facing = Vector3.Dot(n, fwd);
        var shade = Math.Clamp(Math.Abs(Vector3.Dot(n, light)), 0, 1);
        var g = (byte)(40 + 200 * shade);
        var col = new SKColor(g, (byte)(g * 0.95f), (byte)(g * 0.9f));
        if (vertexColors is not null)
        {
            var vc = vertexColors[a]; var k = 0.35f + 0.65f * (float)shade;
            col = new SKColor((byte)(vc.Red * k), (byte)(vc.Green * k), (byte)(vc.Blue * k));
        }
        var (p0, p1, p2) = (proj[a], proj[b], proj[c]);
        int minX = (int)Math.Max(0, Math.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X)))), maxX = (int)Math.Min(size - 1, Math.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))));
        int minY = (int)Math.Max(0, Math.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y)))), maxY = (int)Math.Min(size - 1, Math.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))));
        var area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
        if (Math.Abs(area) < 1e-6f) continue;
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
            depth[y * size + x] = z;
            bmp.SetPixel(x, y, col);
        }
        _ = facing;
    }
    return bmp;
}

static (Vector3[] Positions, int[] Indices) ReadGlb(string path)
{
    var b = File.ReadAllBytes(path);
    var jsonLen = BitConverter.ToInt32(b, 12);
    using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(b, 20, jsonLen));
    var bin = 20 + jsonLen + 8;
    var root = doc.RootElement;
    var prim = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
    (int Offset, int Count, int Stride, int Type) Acc(int i, int defStride)
    {
        var acc = root.GetProperty("accessors")[i];
        var view = root.GetProperty("bufferViews")[acc.GetProperty("bufferView").GetInt32()];
        var off = bin + (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0) + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
        return (off, acc.GetProperty("count").GetInt32(), view.TryGetProperty("byteStride", out var st) ? st.GetInt32() : defStride, acc.GetProperty("componentType").GetInt32());
    }
    var p = Acc(prim.GetProperty("attributes").GetProperty("POSITION").GetInt32(), 12);
    var positions = Enumerable.Range(0, p.Count).Select(i => new Vector3(
        BitConverter.ToSingle(b, p.Offset + i * p.Stride), BitConverter.ToSingle(b, p.Offset + i * p.Stride + 4), BitConverter.ToSingle(b, p.Offset + i * p.Stride + 8))).ToArray();
    var ix = Acc(prim.GetProperty("indices").GetInt32(), 0);
    var indices = Enumerable.Range(0, ix.Count).Select(i => ix.Type switch
    {
        5125 => (int)BitConverter.ToUInt32(b, ix.Offset + i * 4),
        5123 => BitConverter.ToUInt16(b, ix.Offset + i * 2),
        _ => b[ix.Offset + i],
    }).ToArray();
    return (positions, indices);
}
