#:package SkiaSharp@3.119.4
// Local verification only: downloads starchar's male preset reference images and tiles them into one labelled sheet.
using SkiaSharp;

var work = Path.Combine(Path.GetTempPath(), "claude", "c--Users-dougb-Nextcloud-Star-Citizen-TOOLS", "9bed0ac8-1943-47d2-a8fb-f6e0482a5336", "scratchpad", "chf3d", "starchar");
Directory.CreateDirectory(work);
using var http = new HttpClient();
var images = new List<(int Id, SKBitmap Bmp)>();
for (var id = 0; id < 40; id++)
{
    var file = Path.Combine(work, $"{id:00}.webp");
    if (!File.Exists(file))
    {
        var resp = await http.GetAsync($"https://raw.githubusercontent.com/diogotr7/starchar/HEAD/public/m_head_ids/{id:00}.webp");
        if (!resp.IsSuccessStatusCode) continue;
        await File.WriteAllBytesAsync(file, await resp.Content.ReadAsByteArrayAsync());
    }
    var bmp = SKBitmap.Decode(file);
    if (bmp is not null) images.Add((id, bmp));
}
Console.WriteLine($"Got {images.Count} images, size {images[0].Bmp.Width}x{images[0].Bmp.Height}");

const int Cell = 200, Cols = 9;
var rows = (images.Count + Cols - 1) / Cols;
using var sheet = new SKBitmap(Cols * Cell, rows * (Cell + 24));
using (var c = new SKCanvas(sheet))
{
    c.Clear(new SKColor(16, 20, 28));
    using var font = new SKFont(SKTypeface.Default, 18); using var paint = new SKPaint { Color = SKColors.White };
    for (var k = 0; k < images.Count; k++)
    {
        var (id, bmp) = images[k];
        float x = k % Cols * Cell, y = k / Cols * (Cell + 24);
        c.DrawBitmap(bmp, new SKRect(x, y + 24, x + Cell, y + 24 + Cell));
        c.DrawText($"id {id}", x + 6, y + 18, font, paint);
    }
}
using var fs = File.Create(Path.Combine(work, "..", "starchar_male_ids.png"));
sheet.Encode(fs, SKEncodedImageFormat.Png, 90);
Console.WriteLine("Wrote starchar_male_ids.png");
