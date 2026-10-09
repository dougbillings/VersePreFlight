using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VersePreflight.App.Theme;

namespace VersePreflight.App.Controls;

/// <summary>Window backdrop: dark base, a faint cyan glow from the top and a fine grid.</summary>
public sealed class HudBackground : Control
{
    private const double GridSize = 40;

    private static readonly IBrush Glow = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x26, Hud.Accent.R, Hud.Accent.G, Hud.Accent.B), 0),
            new GradientStop(Color.FromArgb(0x00, Hud.Accent.R, Hud.Accent.G, Hud.Accent.B), 0.45),
        },
    };

    private static readonly Pen GridPen = new(Hud.GridLineBrush, 1);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Hud.Bg0Brush, bounds);
        context.FillRectangle(Glow, bounds);

        for (var x = GridSize; x < bounds.Width; x += GridSize)
            context.DrawLine(GridPen, new Point(x + 0.5, 0), new Point(x + 0.5, bounds.Height));
        for (var y = GridSize; y < bounds.Height; y += GridSize)
            context.DrawLine(GridPen, new Point(0, y + 0.5), new Point(bounds.Width, y + 0.5));
    }
}
