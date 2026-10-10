#:project C:\Users\dougb\source\repos\VersePreflight\src\VersePreflight.App\VersePreflight.App.csproj
#:package SkiaSharp@3.119.4
#:property AllowUnsafeBlocks=true
using System.Numerics;
using SkiaSharp;
using VersePreflight.App.Rendering;
using VersePreflight.Core.Characters;

// Fits the studio light intensities to the in-game shots. Each light component is rendered on its own in
// linear light; skin regions at matching face positions (aligned by the pupils) give one equation per
// region, character and channel; non-negative least squares gives the intensities. args: work, images, modes
var (work, images) = (args[0], args[1]);
var modes = args.Length > 2 ? args[2].Split(',') : ["mean", "source", "detail"];
var p4k = @"G:\Program Files\Roberts Space Industries\StarCitizen\LIVE\Data.p4k";
var cacheDir = Path.Combine(work, "cache");
var libraries = new HeadLibraryCache(cacheDir).GetOrBuild(p4k, "LIVE-12881860");
using var accessories = AccessoryLibrary.Open(p4k, cacheDir, "LIVE-12881860");
string[] names = ["BACKHAND", "gomab", "gomabptu", "redshirt"];
var gameLeft = new Vector2(1311, 711); var gameRight = new Vector2(1556, 710);
SKRectI[] regions =
[
    new(1380, 535, 1490, 595), new(1290, 545, 1360, 595), new(1510, 545, 1580, 595), // forehead centre, left, right
    new(1415, 690, 1450, 730),                                                       // between the eyes
    new(1265, 765, 1335, 805), new(1530, 765, 1600, 805),                            // under-eye cheeks
    new(1405, 760, 1460, 800), new(1410, 830, 1455, 860),                            // nose bridge, nose tip
    new(1225, 690, 1255, 760), new(1610, 690, 1640, 760),                            // temples by the eyes
];
var dirs = new List<Vector2>();
foreach (var el in new[] { -30f, 0f, 30f, 60f }) foreach (var az in new[] { -90f, -60f, -30f, 0f, 30f, 60f, 90f }) dirs.Add(new(az, el));
dirs.Add(new(0, 90));
var compList = new List<(string Name, HeadLighting L, int Dir, bool Spec)>();
for (var d = 0; d < dirs.Count; d++)
{
    compList.Add(($"dir {dirs[d]} diffuse", Single(dirs[d]) with { Specular = 0, Reflection = 0 }, d, false));
    compList.Add(($"dir {dirs[d]} highlight", Single(dirs[d]) with { Diffuse = 0, Reflection = 0 }, d, true));
}
compList.Add(("sky diffuse", Only(sky: true) with { Specular = 0, Reflection = 0 }, -1, false));
compList.Add(("ground diffuse", Only(ground: true) with { Specular = 0, Reflection = 0 }, -2, false));
compList.Add(("sky reflection", Only(sky: true) with { Diffuse = 0, Specular = 0 }, -3, false));
compList.Add(("ground reflection", Only(ground: true) with { Diffuse = 0, Specular = 0 }, -4, false));
var components = compList.Select(c => (c.Name, c.L)).ToArray();
static HeadLighting Single(Vector2 angles) => new() { Lights = [new StudioLight(angles, Vector3.One)], Sky = Vector3.Zero, Ground = Vector3.Zero, ToneMap = false, Reflection = 1, SkinHighlights = 1 };
static HeadLighting Only(bool sky = false, bool ground = false) => new()
{
    Lights = [], Sky = sky ? Vector3.One : Vector3.Zero, Ground = ground ? Vector3.One : Vector3.Zero, ToneMap = false, Reflection = 1, SkinHighlights = 1,
};
static float Lin(byte v) => MathF.Pow(v / 255f, 2.2f);
static float InverseTone(float y)
{
    y = Math.Clamp(y, 0, 0.98f);
    float a = 2.51f - 2.43f * y, b = 0.03f - 0.59f * y, c = -0.14f * y;
    return (-b + MathF.Sqrt(b * b - 4 * a * c)) / (2 * a);
}
static float Filmic(float x) { x = MathF.Max(0, x); return Math.Clamp(x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f), 0, 1); }
static int Srgb(float lin) => (int)MathF.Round(MathF.Pow(Math.Clamp(lin, 0, 1), 1 / 2.2f) * 255);

// In-game targets per region (pre-tone-mapping linear, assuming a filmic curve like ours)
var targets = new List<Vector3>();
var targetSrgb = new List<Vector3>();
for (var n = 0; n < names.Length; n++)
{
    using var game = SKBitmap.Decode(Path.Combine(images, $"{n + 1}.jpg"));
    foreach (var r in regions)
    {
        var sum = Vector3.Zero; var k = 0;
        for (var y = r.Top; y < r.Bottom; y++) for (var x = r.Left; x < r.Right; x++) { var c = game.GetPixel(x, y); sum += new Vector3(Lin(c.Red), Lin(c.Green), Lin(c.Blue)); k++; }
        var mean = sum / k;
        targetSrgb.Add(mean);
        targets.Add(new Vector3(InverseTone(mean.X), InverseTone(mean.Y), InverseTone(mean.Z)));
    }
}

foreach (var mode in modes)
{
    // responses[component][equation] (linear, per channel)
    var responses = components.Select(_ => new List<Vector3>()).ToArray();
    for (var n = 0; n < names.Length; n++)
    {
        var character = CharacterFile.Read(Path.Combine(work, "..", "chf3d", names[n] + ".chf"));
        var library = libraries.First(l => l.BodyHash == character.BodyHash);
        var parts = BuildParts(library, character, accessories, mode);
        const int W = 900, H = 900;
        var renderer = new HeadRenderer();
        renderer.SetParts(parts);
        var (scale, ourMid, gameMid) = Align(parts, W, H, gameLeft, gameRight);
        var pixels = new uint[W * H];
        for (var ci = 0; ci < components.Length; ci++)
        {
            renderer.Lighting = components[ci].L;
            renderer.Render(pixels, W, H, 0, 0, 1.3f, 1);
            foreach (var r in regions)
            {
                var sum = Vector3.Zero; var k = 0;
                for (var y = r.Top; y < r.Bottom; y++)
                    for (var x = r.Left; x < r.Right; x++)
                    {
                        // game pixel -> our pixel
                        var o = (new Vector2(x, y) - gameMid) / scale + ourMid;
                        int ox = (int)o.X, oy = (int)o.Y;
                        if (ox < 0 || oy < 0 || ox >= W || oy >= H) continue;
                        var px = pixels[oy * W + ox];
                        if (px >> 24 < 250) continue;
                        sum += new Vector3(Lin((byte)(px >> 16)), Lin((byte)(px >> 8)), Lin((byte)px)); k++;
                    }
                responses[ci].Add(k > 0 ? sum / k : Vector3.Zero);
            }
        }
    }

    // Model: each direction contributes I_d * (diffuse + s * highlight) with one shared highlight strength s;
    // plus sky/ground diffuse and reflections. Non-negative least squares per channel, for a range of s.
    var eqs = targets.Count;
    var best = (Err: double.MaxValue, S: 0f, W: Array.Empty<Vector3>(), Basis: new List<Vector3>[0], Names: Array.Empty<string>());
    foreach (var sGlobal in new[] { 1f, 2f, 3f, 4f, 6f })
    {
        // Combined basis: light groups (each direction diffuse + s * highlight, one colour per group), then ambient terms
        var basisR = new List<List<Vector3>>(); var basisNames = new List<string>();
        (string Name, Vector2[] Dirs)[] groups =
        [
            ("softbox right", [new(60, -30), new(90, -30), new(60, 0), new(90, 0), new(60, 30), new(90, 30)]),
            ("top front", [new(0, 30), new(0, 60)]),
            ("fill left", [new(-60, 0), new(-90, 0)]),
            ("front low", [new(0, -30), new(-30, -30), new(30, -30)]),
        ];
        foreach (var (gname, gdirs) in groups)
        {
            var sum = Enumerable.Repeat(Vector3.Zero, eqs).ToList();
            foreach (var gd in gdirs)
            {
                var d = dirs.IndexOf(gd);
                var diff = responses[compList.FindIndex(c => c.Dir == d && !c.Spec)];
                var spec = responses[compList.FindIndex(c => c.Dir == d && c.Spec)];
                for (var e = 0; e < eqs; e++) sum[e] += (diff[e] + sGlobal * spec[e]) / gdirs.Length;
            }
            basisR.Add(sum); basisNames.Add(gname);
        }
        foreach (var k in new[] { -1, -2, -3, -4 }) { var i = compList.FindIndex(c => c.Dir == k); basisR.Add(responses[i]); basisNames.Add(compList[i].Name); }
        var m = basisR.Count;
        // Neutral light intensities a_k (shared by R, G, B) and one white balance per channel, fitted alternately
        var a = new double[m]; for (var j = 0; j < m; j++) a[j] = 0.2;
        var wb = new double[] { 1, 1, 1 };
        double G(Vector3 v, int ch) => ch == 0 ? v.X : ch == 1 ? v.Y : v.Z;
        for (var round = 0; round < 60; round++)
        {
            // intensities, all channels together
            for (var iter = 0; iter < 200; iter++)
                for (var j = 0; j < m; j++)
                {
                    double num = 0, den = 0;
                    for (var e = 0; e < eqs; e++)
                        for (var ch = 0; ch < 3; ch++)
                        {
                            var pred = 0.0; for (var i = 0; i < m; i++) if (i != j) pred += a[i] * G(basisR[i][e], ch);
                            var x = wb[ch] * G(basisR[j][e], ch);
                            num += x * (G(targets[e], ch) - wb[ch] * pred); den += x * x;
                        }
                    a[j] = den > 1e-12 ? Math.Max(0, num / den) : 0;
                }
            // white balance per channel, normalised so green = 1 (overall level stays in the intensities)
            for (var ch = 0; ch < 3; ch++)
            {
                double num = 0, den = 0;
                for (var e = 0; e < eqs; e++) { var pred = 0.0; for (var i = 0; i < m; i++) pred += a[i] * G(basisR[i][e], ch); num += pred * G(targets[e], ch); den += pred * pred; }
                wb[ch] = num / den;
            }
            var g = wb[1]; for (var ch = 0; ch < 3; ch++) wb[ch] /= g; for (var j = 0; j < m; j++) a[j] *= g;
        }
        var weights = new Vector3[m];
        for (var j = 0; j < m; j++) weights[j] = new Vector3((float)(a[j] * wb[0]), (float)(a[j] * wb[1]), (float)(a[j] * wb[2]));
        Console.WriteLine($"  white balance {wb[0]:0.000} {wb[1]:0.000} {wb[2]:0.000}; intensities " + string.Join(", ", basisNames.Select((n, j) => $"{n} {a[j]:0.000}")));
        double err = 0;
        for (var e = 0; e < eqs; e++)
        {
            var p = Vector3.Zero; for (var j = 0; j < m; j++) p += weights[j] * basisR[j][e];
            var shown = new Vector3(Srgb(Filmic(p.X)), Srgb(Filmic(p.Y)), Srgb(Filmic(p.Z)));
            var game = new Vector3(Srgb(targetSrgb[e].X), Srgb(targetSrgb[e].Y), Srgb(targetSrgb[e].Z));
            err += Vector3.DistanceSquared(shown, game);
        }
        err = Math.Sqrt(err / eqs / 3);
        Console.WriteLine($"  {mode} s={sGlobal}: RMS {err:0.0}");
        if (err < best.Err) best = (err, sGlobal, weights, basisR.ToArray(), basisNames.ToArray());
    }
    Console.WriteLine($"== {mode}: best s={best.S} RMS {best.Err:0.0}");
    for (var j = 0; j < best.W.Length; j++)
        if (best.W[j].Length() > 0.02f) Console.WriteLine($"  {best.Names[j],-22} {best.W[j].X:0.000} {best.W[j].Y:0.000} {best.W[j].Z:0.000}");
    for (var e = 0; e < eqs; e++)
    {
        if (e % regions.Length > 3) continue;
        var p = Vector3.Zero; for (var j = 0; j < best.W.Length; j++) p += best.W[j] * best.Basis[j][e];
        Console.WriteLine($"    {names[e / regions.Length],-9} r{e % regions.Length}: game {new Vector3(Srgb(targetSrgb[e].X), Srgb(targetSrgb[e].Y), Srgb(targetSrgb[e].Z))}  fit {new Vector3(Srgb(Filmic(p.X)), Srgb(Filmic(p.Y)), Srgb(Filmic(p.Z)))}");
    }
}

static (float Scale, Vector2 OurMid, Vector2 GameMid) Align(List<MeshPart> parts, int W, int H, Vector2 gameLeft, Vector2 gameRight)
{
    var head = parts[0].Positions; var top = head.Max(v => v.Z);
    var near = head.Where(v => v.Z > top - 0.25f).ToArray();
    var min = new Vector3(near.Min(v => v.X), near.Min(v => v.Y), near.Min(v => v.Z)); var max = new Vector3(near.Max(v => v.X), near.Max(v => v.Y), near.Max(v => v.Z));
    var focus = (min + max) / 2; var radius = (max - min).Length() / 2;
    const float fov = 28f * MathF.PI / 180f;
    var camera = focus + new Vector3(0, 1, 0) * (radius / MathF.Sin(fov / 2) * 0.9f / 1.3f);
    var f = Vector3.Normalize(focus - camera); var right = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ)); var up = Vector3.Cross(right, f);
    var focal = H / (2 * MathF.Tan(fov / 2));
    Vector2 Project(Vector3 p) { var rel = p - camera; var z = Vector3.Dot(rel, f); return new(W / 2f + Vector3.Dot(rel, right) / z * focal, H / 2f - Vector3.Dot(rel, up) / z * focal); }
    var eyes = parts[1].Positions; var mid = eyes.Average(v => v.X);
    var e1 = Project(eyes.Where(v => v.X < mid).Aggregate(Vector3.Zero, (a, b) => a + b) / eyes.Count(v => v.X < mid));
    var e2 = Project(eyes.Where(v => v.X >= mid).Aggregate(Vector3.Zero, (a, b) => a + b) / eyes.Count(v => v.X >= mid));
    var (l, r) = e1.X < e2.X ? (e1, e2) : (e2, e1);
    return (Vector2.Distance(gameLeft, gameRight) / Vector2.Distance(l, r), (l + r) / 2, (gameLeft + gameRight) / 2);
}

static SkinSurface Tone(SkinMaterial m, Vector3 skin, string mode)
{
    static float Lin(float v) => MathF.Pow(v, 2.2f);
    var body = new Vector3(Lin(skin.X), Lin(skin.Y), Lin(skin.Z));
    var src = m.SourceAverageColor;
    return mode switch
    {
        "source" => new SkinSurface(m, body / src),
        "detail" => new SkinSurface(m, body / new Vector3(MathF.Pow(src.X, m.SourceDetail), MathF.Pow(src.Y, m.SourceDetail), MathF.Pow(src.Z, m.SourceDetail))) { Contrast = m.SourceDetail },
        _ => new SkinSurface(m, CharacterColoring.SkinTone(m, skin)),
    };
}

// Same as CharactersViewModel.BuildParts
static List<MeshPart> BuildParts(HeadLibrary library, CharacterFile character, AccessoryLibrary accessories, string toneMode)
{
    var head = library.Head.Blend(character);
    var skin = character.SkinColor?.ToVector() ?? CharacterColoring.DefaultSkin;
    var material = accessories.LoadSkinMaterial(character.HeadMaterialId);
    Surface skinSurface = material is null ? new PlainSurface(0.55f, 0.03f) : Tone(material, skin, toneMode);

    var parts = new List<MeshPart> { new(head, library.Head.Indices, CharacterColoring.Uniform(head.Length, skin), skinSurface) { Uvs = library.Head.Uvs } };
    if (library.Eyes is { } eyesSet)
    {
        var eyes = eyesSet.Blend(character);
        var iris = character.EyeColor?.ToVector() ?? new Vector3(0.35f, 0.25f, 0.15f);
        parts.Add(new MeshPart(eyes, eyesSet.Indices, CharacterColoring.Eyes(eyes, iris), new PlainSurface(0.12f) { AmbientOcclusion = 0.55f }));
    }
    var equipped = accessories.LoadFor(character, library.IsFemale);
    var roots = equipped.SelectMany(e => CharacterColoring.StrandVertices(e.Mesh)).ToList();
    if (roots.Count > 0)
    {
        var occ = CharacterColoring.SkinOcclusion(head, roots);
        var sorted = occ.Where(o => o < 0.999f).OrderBy(o => o).ToArray();

        parts[0] = parts[0] with { Occlusion = occ };
    }
    foreach (var (kind, item) in equipped)
    {
        var choice = kind switch { AccessoryKind.Beard => character.Beard, AccessoryKind.Eyebrows => character.Eyebrows, _ => character.Hair };
        var colors = CharacterColoring.Hair(item.Mesh.Uvs, choice, item.DefaultMelanin);
        var natural = CharacterColoring.Uniform(item.Mesh.Positions.Length, CharacterColoring.NaturalHair(choice, item.DefaultMelanin));
        var strands = item.Layers.FirstOrDefault(l => l.Kind == HairLayerKind.Strands);
        var occlusion = strands is null ? null : CharacterColoring.HairOcclusion(item.Mesh.Positions, head, strands.Shading.AmbientOcclusion);

        foreach (var layer in item.Layers)
            parts.Add(layer.Kind == HairLayerKind.Cap
                ? new MeshPart(item.Mesh.Positions, layer.Indices, natural, new CapSurface(layer.Opacity)) { Uvs = item.Mesh.Uvs }
                : new MeshPart(item.Mesh.Positions, layer.Indices, colors, new HairSurface(layer)) { Uvs = item.Mesh.Uvs, Occlusion = occlusion });
    }
    return parts;
}
