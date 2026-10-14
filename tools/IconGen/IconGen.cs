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
