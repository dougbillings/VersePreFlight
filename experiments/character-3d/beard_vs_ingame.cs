#:project C:\Users\dougb\source\repos\VersePreflight\src\VersePreflight.App\VersePreflight.App.csproj
#:package SkiaSharp@3.119.4
#:property AllowUnsafeBlocks=true
using System.Numerics;
using SkiaSharp;
using VersePreflight.App.Rendering;
using VersePreflight.Core.Characters;

// Renders each test character head-on and aligns it to the in-game shot by the pupils, then compares
// regions at the same face positions. args: work, images, out.png, tone mode, [pitch]
var (work, images, outFile, toneMode) = (args[0], args[1], args[2], args[3]);
var pitch = args.Length > 4 ? float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 0f;
float Arg(int i, float d) => args.Length > i ? float.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture) : d;
var (boostK, capK, occK) = (Arg(5, 0.4f), Arg(6, 1.6f), Arg(7, 1f));
OcclusionStrength = occK;
var p4k = @"G:\Program Files\Roberts Space Industries\StarCitizen\LIVE\Data.p4k";
var cacheDir = Path.Combine(work, "cache");
var libraries = new HeadLibraryCache(cacheDir).GetOrBuild(p4k, "LIVE-12881860");
using var accessories = AccessoryLibrary.Open(p4k, cacheDir, "LIVE-12881860");
string[] names = ["BACKHAND", "gomab", "gomabptu", "redshirt"];
var gameLeft = new Vector2(1311, 711); var gameRight = new Vector2(1556, 710);
const int X0 = 1130, Y0 = 470, CW = 600, CH = 760; const float PS = 0.75f;
int pw = (int)(CW * PS), ph = (int)(CH * PS);
using var sheet = new SKBitmap(pw * 3, ph * names.Length);
using var canvas = new SKCanvas(sheet);
canvas.Clear(SKColors.Black);
(string Name, SKRectI Box)[] regions = [("moustache", new(1395, 928, 1470, 950)), ("beside mouth L", new(1265, 940, 1310, 1000)), ("beside mouth R", new(1560, 940, 1605, 1000)), ("chin", new(1395, 1070, 1470, 1120)), ("cheek R", new(1530, 765, 1600, 805))];
for (var n = 0; n < names.Length; n++)
{
    var character = CharacterFile.Read(Path.Combine(work, "..", "chf3d", names[n] + ".chf"));
    var library = libraries.First(l => l.BodyHash == character.BodyHash);
    var parts = CharacterScene.Build(library, character, accessories, new SceneOptions { FollowFace = Arg(8, 1) > 0, FacialHairShading = occK });
    const int W = 1200, H = 1200;
    var renderer = new HeadRenderer { StrandOpacityBoost = boostK, CapOpacity = capK };
    renderer.SetParts(parts);
    var pixels = new uint[W * 2 * H * 2];
    renderer.Render(pixels, W * 2, H * 2, 0, pitch, 1.3f, 2);
    using var ours = new SKBitmap(new SKImageInfo(W * 2, H * 2, SKColorType.Bgra8888, SKAlphaType.Premul));
    unsafe { fixed (uint* p = pixels) Buffer.MemoryCopy(p, (void*)ours.GetPixels(), pixels.Length * 4L, pixels.Length * 4L); }

    // Same camera as HeadRenderer, to find where the eyes land
    var head = parts[0].Positions; var top = head.Max(v => v.Z);
    var near = head.Where(v => v.Z > top - 0.25f).ToArray();
    var min = new Vector3(near.Min(v => v.X), near.Min(v => v.Y), near.Min(v => v.Z)); var max = new Vector3(near.Max(v => v.X), near.Max(v => v.Y), near.Max(v => v.Z));
    var focus = (min + max) / 2; var radius = (max - min).Length() / 2;
    const float fov = 28f * MathF.PI / 180f;
    var camera = focus + new Vector3(0, MathF.Cos(pitch), MathF.Sin(pitch)) * (radius / MathF.Sin(fov / 2) * 0.9f / 1.3f);
    var f = Vector3.Normalize(focus - camera); var right = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ)); var up = Vector3.Cross(right, f);
    var focal = H * 2 / (2 * MathF.Tan(fov / 2));
    Vector2 Project(Vector3 p) { var rel = p - camera; var z = Vector3.Dot(rel, f); return new(W + Vector3.Dot(rel, right) / z * focal, H - Vector3.Dot(rel, up) / z * focal); }
    var eyes = parts[1].Positions; var mid = eyes.Average(v => v.X);
    var e1 = Project(eyes.Where(v => v.X < mid).Aggregate(Vector3.Zero, (a, b) => a + b) / eyes.Count(v => v.X < mid));
    var e2 = Project(eyes.Where(v => v.X >= mid).Aggregate(Vector3.Zero, (a, b) => a + b) / eyes.Count(v => v.X >= mid));
    var (ourLeft, ourRight) = e1.X < e2.X ? (e1, e2) : (e2, e1);
    var scale = Vector2.Distance(gameLeft, gameRight) / Vector2.Distance(ourLeft, ourRight);
    var ourMid = (ourLeft + ourRight) / 2; var gameMid = (gameLeft + gameRight) / 2;

    // Our render in game-screenshot coordinates
    using var aligned = new SKBitmap(CW, CH);
    using (var c = new SKCanvas(aligned))
    {
        c.Clear(new SKColor(10, 16, 24));
        c.Translate(gameMid.X - X0, gameMid.Y - Y0); c.Scale(scale); c.Translate(-ourMid.X, -ourMid.Y);
        c.DrawImage(SKImage.FromBitmap(ours), 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }
    using var game = SKBitmap.Decode(Path.Combine(images, $"{n + 1}.jpg"));
    using var gameCrop = new SKBitmap(CW, CH);
    using (var c = new SKCanvas(gameCrop)) c.DrawBitmap(game, new SKRect(X0, Y0, X0 + CW, Y0 + CH), new SKRect(0, 0, CW, CH));

    var y = n * ph;
    canvas.DrawBitmap(gameCrop, new SKRect(0, y, pw, y + ph));
    canvas.DrawBitmap(aligned, new SKRect(pw, y, 2 * pw, y + ph));
    canvas.DrawBitmap(gameCrop, new SKRect(2 * pw, y, 3 * pw, y + ph));
    canvas.DrawBitmap(aligned, new SKRect(2 * pw, y, 3 * pw, y + ph), new SKPaint { Color = new SKColor(255, 255, 255, 128) });

    string Mean(SKBitmap b, SKRectI r)
    {
        double R = 0, G = 0, B = 0; var k = 0;
        for (var yy = r.Top - Y0; yy < r.Bottom - Y0; yy++) for (var xx = r.Left - X0; xx < r.Right - X0; xx++) { var px = b.GetPixel(xx, yy); R += px.Red; G += px.Green; B += px.Blue; k++; }
        return $"{(int)(R / k),3},{(int)(G / k),3},{(int)(B / k),3}";
    }
    Console.WriteLine($"{names[n],-9} {string.Join("  ", regions.Select(r => $"{r.Name}: game {Mean(gameCrop, r.Box)} ours {Mean(aligned, r.Box)}"))}");
    var box = new SKPaint { Color = SKColors.Yellow, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    foreach (var (_, r) in regions) canvas.DrawRect(new SKRect(pw + (r.Left - X0) * PS, y + (r.Top - Y0) * PS, pw + (r.Right - X0) * PS, y + (r.Bottom - Y0) * PS), box);
}
using (var fs = File.Create(outFile)) sheet.Encode(fs, SKEncodedImageFormat.Png, 90);

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

partial class Program { public static float OcclusionStrength = 1; }
