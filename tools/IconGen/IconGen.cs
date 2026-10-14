#:package SkiaSharp@3.119.4
// Generates the Verse Preflight app icon (Windows .ico + PNGs) from code, so it can be
// rebuilt or tweaked without design tools. Run from the repo root:
//   dotnet run tools/IconGen/IconGen.cs
// The mark matches the in-app LogoMark: a diamond frame with a launch chevron.
using SkiaSharp;

var repo = Directory.GetCurrentDirectory();
var icoPath = Path.Combine(repo, "src", "VersePreflight.App", "Assets", "app.ico");
var pngDir = Path.Combine(repo, "assets");
Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
Directory.CreateDirectory(pngDir);

int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var images = icoSizes.Select(size => (size, png: Render(size))).ToList();
WriteIco(icoPath, images);
File.WriteAllBytes(Path.Combine(pngDir, "icon-256.png"), Render(256));
File.WriteAllBytes(Path.Combine(pngDir, "icon-512.png"), Render(512));
Console.WriteLine($"Wrote {icoPath} ({images.Count} sizes) and PNGs in {pngDir}");

// Microsoft Store (MSIX) logos. The targetsize variants are picked by Windows for the taskbar,
// Start and Explorer; "altform-unplated" means drawn without a coloured plate behind it.
var msixDir = Path.Combine(repo, "packaging", "msix", "Assets");
Directory.CreateDirectory(msixDir);
File.WriteAllBytes(Path.Combine(msixDir, "StoreLogo.png"), Render(50));
File.WriteAllBytes(Path.Combine(msixDir, "Square150x150Logo.png"), Render(150));
File.WriteAllBytes(Path.Combine(msixDir, "Square44x44Logo.png"), Render(44));
foreach (var size in (int[])[16, 24, 32, 48, 256])
{
    var png = Render(size);
    File.WriteAllBytes(Path.Combine(msixDir, $"Square44x44Logo.targetsize-{size}.png"), png);
    File.WriteAllBytes(Path.Combine(msixDir, $"Square44x44Logo.targetsize-{size}_altform-unplated.png"), png);
}
Console.WriteLine($"Wrote Store logos in {msixDir}");

// Optional: "--store-art <folder>" writes the Microsoft Store listing images (not committed; regenerate
// when needed): the 300x300 app tile icon and the 16:9 "super hero" art, which must have no text or UI.
if (args is ["--store-art", var storeArtDir])
{
    Directory.CreateDirectory(storeArtDir);
    File.WriteAllBytes(Path.Combine(storeArtDir, "app-tile-icon-300x300.png"), Render(300));
    File.WriteAllBytes(Path.Combine(storeArtDir, "super-hero-art-3840x2160.png"), RenderHero(3840, 2160));
    Console.WriteLine($"Wrote Store listing art in {Path.GetFullPath(storeArtDir)}");
}

// Optional: "--preview <file.png>" writes the small sizes enlarged (pixelated) on dark and light
// backgrounds, to judge how the icon reads in a taskbar or title bar.
if (args is ["--preview", var previewPath])
{
    int[] previewSizes = [16, 24, 32, 48, 64];
    const int scale = 4, pad = 16;
    var width = previewSizes.Sum(s => s * scale + pad) + pad;
    var height = 2 * (64 * scale + pad) + pad;
    using var sheet = SKSurface.Create(new SKImageInfo(width, height));
    var c = sheet.Canvas;
    c.Clear(SKColor.Parse("#1F1F1F"));
    using (var light = new SKPaint { Color = SKColor.Parse("#F3F3F3") })
        c.DrawRect(0, 64 * scale + pad + pad / 2f, width, height, light);
    var x = pad;
    foreach (var s in previewSizes)
    {
        using var img = SKImage.FromEncodedData(Render(s));
        for (var row = 0; row < 2; row++)
        {
            var y = pad + row * (64 * scale + pad);
            c.DrawImage(img, new SKRect(x, y, x + s * scale, y + s * scale), new SKSamplingOptions(SKFilterMode.Nearest));
        }
        x += s * scale + pad;
    }
    using var snapshot = sheet.Snapshot();
    using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(previewPath, encoded.ToArray());
    Console.WriteLine($"Preview: {previewPath}");
}

static byte[] Render(int size)
{
    var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info);
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / 256f);

    var small = size <= 24; // tiny sizes: drop the outer frame and thicken strokes so it stays legible
    var accent = SKColor.Parse("#3FD8F2");
    var accentBright = SKColor.Parse("#8BEBFA");
    var accentDim = SKColor.Parse("#1D6F82");

    // Tile: chamfered square matching the app's HUD frames
    const float cut = 46;
    using var tile = new SKPath();
    tile.MoveTo(cut, 4);
    tile.LineTo(252, 4);
    tile.LineTo(252, 252 - cut);
    tile.LineTo(252 - cut, 252);
    tile.LineTo(4, 252);
    tile.LineTo(4, cut);
    tile.Close();

    using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill })
    {
        fill.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 256),
            [SKColor.Parse("#12263A"), SKColor.Parse("#070B12")], SKShaderTileMode.Clamp);
        canvas.DrawPath(tile, fill);
    }
    using (var edge = Stroke(accentDim, small ? 14 : 6))
        canvas.DrawPath(tile, edge);

    if (!small)
    {
        using var outer = Diamond(128, 128, 100);
        using var p = Stroke(accentDim, 8);
        canvas.DrawPath(outer, p);
    }

    using (var inner = Diamond(128, 128, small ? 92 : 74))
    using (var p = Stroke(accent, small ? 20 : 10))
        canvas.DrawPath(inner, p);

    using (var chevron = new SKPath())
    using (var p = Stroke(accentBright, small ? 30 : 15))
    {
        p.StrokeCap = SKStrokeCap.Butt;
        p.StrokeJoin = SKStrokeJoin.Miter;
        chevron.MoveTo(small ? 78 : 90, small ? 156 : 146);
        chevron.LineTo(128, small ? 106 : 108);
        chevron.LineTo(small ? 178 : 166, small ? 156 : 146);
        canvas.DrawPath(chevron, p);
    }

    if (!small)
    {
        using var dot = new SKPaint { IsAntialias = true, Color = accent };
        canvas.DrawRect(121, 160, 14, 14, dot);
    }

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// Store "super hero" art: deep space, a seeded starfield and the launch mark glowing among radar-like
// diamond rings. Drawn entirely from code (no game assets). Microsoft asks for no text and no UI, and
// for key visuals to stay out of the bottom third, where the Store may overlay a gradient.
static byte[] RenderHero(int width, int height)
{
    using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    var unit = height / 2160f;
    var cx = width / 2f;
    var cy = height * 0.4f; // upper two-thirds

    using (var sky = new SKPaint())
    {
        sky.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, height),
            [SKColor.Parse("#0C1C2E"), SKColor.Parse("#03060B")], SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, width, height, sky);
    }
    using (var nebula = new SKPaint())
    {
        nebula.Shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), 1500 * unit,
            [SKColor.Parse("#3FD8F2").WithAlpha(46), SKColor.Parse("#1D6F82").WithAlpha(20), SKColors.Transparent],
            [0f, 0.45f, 1f], SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, width, height, nebula);
    }

    // Stars: fixed seed so every run produces the same image
    var random = new Random(4101);
    using (var star = new SKPaint { IsAntialias = true })
    {
        for (var i = 0; i < 1600; i++)
        {
            var radius = (float)Math.Pow(random.NextDouble(), 4) * 4.5f * unit + 0.8f * unit;
            var tint = random.NextDouble() < 0.2 ? SKColor.Parse("#BFF4FF") : SKColor.Parse("#F2F6FF");
            star.Color = tint.WithAlpha((byte)random.Next(60, 230));
            canvas.DrawCircle((float)random.NextDouble() * width, (float)random.NextDouble() * height, radius, star);
        }
    }

    // Radar-like diamond rings, fading outwards
    for (var ring = 1; ring <= 7; ring++)
    {
        using var path = Diamond(cx, cy, (360 + ring * 190) * unit);
        using var paint = Stroke(SKColor.Parse("#3FD8F2").WithAlpha((byte)(70 - ring * 8)), 3 * unit);
        canvas.DrawPath(path, paint);
    }

    // The mark (same geometry as the app icon, scaled up), with a soft glow behind it
    var scale = 4.2f * unit;
    SKPoint P(float x, float y) => new(cx + (x - 128) * scale, cy + (y - 128) * scale);
    void DrawMark(SKMaskFilter? glow)
    {
        using (var outer = Diamond(cx, cy, 100 * scale))
        using (var p = Stroke(SKColor.Parse("#1D6F82"), 8 * scale))
        {
            p.MaskFilter = glow;
            canvas.DrawPath(outer, p);
        }
        using (var inner = Diamond(cx, cy, 74 * scale))
        using (var p = Stroke(SKColor.Parse("#3FD8F2"), 10 * scale))
        {
            p.MaskFilter = glow;
            canvas.DrawPath(inner, p);
        }
        using (var chevron = new SKPath())
        using (var p = Stroke(SKColor.Parse("#8BEBFA"), 15 * scale))
        {
            p.MaskFilter = glow;
            p.StrokeCap = SKStrokeCap.Butt;
            chevron.MoveTo(P(90, 146));
            chevron.LineTo(P(128, 108));
            chevron.LineTo(P(166, 146));
            canvas.DrawPath(chevron, p);
        }
        using (var dot = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#3FD8F2"), MaskFilter = glow })
        {
            var corner = P(121, 160);
            canvas.DrawRect(corner.X, corner.Y, 14 * scale, 14 * scale, dot);
        }
    }
    using (var glow = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 38 * unit))
        DrawMark(glow);
    DrawMark(null);

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

static SKPaint Stroke(SKColor color, float width) =>
    new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color, StrokeWidth = width, StrokeJoin = SKStrokeJoin.Miter };

static SKPath Diamond(float cx, float cy, float r)
{
    var path = new SKPath();
    path.MoveTo(cx, cy - r);
    path.LineTo(cx + r, cy);
    path.LineTo(cx, cy + r);
    path.LineTo(cx - r, cy);
    path.Close();
    return path;
}

// ICO container with PNG-compressed entries (supported since Windows Vista)
static void WriteIco(string path, List<(int size, byte[] png)> images)
{
    using var stream = File.Create(path);
    using var w = new BinaryWriter(stream);
    w.Write((ushort)0);            // reserved
    w.Write((ushort)1);            // type: icon
    w.Write((ushort)images.Count);
    var offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);          // palette colours
        w.Write((byte)0);          // reserved
        w.Write((ushort)1);        // colour planes
        w.Write((ushort)32);       // bits per pixel
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images) w.Write(png);
}
