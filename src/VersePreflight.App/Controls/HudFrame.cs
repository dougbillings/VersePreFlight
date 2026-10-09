using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VersePreflight.App.Controls;

/// <summary>
/// A panel with cut (chamfered) top-left and bottom-right corners and an accent
/// stroke along the top-left cut: the basic HUD frame used by panels and buttons.
/// </summary>
public sealed class HudFrame : Decorator
{
    public static readonly StyledProperty<double> CutProperty =
        AvaloniaProperty.Register<HudFrame, double>(nameof(Cut), 12);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<HudFrame, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<HudFrame, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<HudFrame, double>(nameof(StrokeThickness), 1);

    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<HudFrame, IBrush?>(nameof(Accent));

    static HudFrame() =>
        AffectsRender<HudFrame>(CutProperty, FillProperty, StrokeProperty, StrokeThicknessProperty, AccentProperty);

    public double Cut { get => GetValue(CutProperty); set => SetValue(CutProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public override void Render(DrawingContext context)
    {
        var half = StrokeThickness / 2;
        var r = new Rect(Bounds.Size).Deflate(half);
        if (r.Width <= 0 || r.Height <= 0) return;
        var c = Math.Min(Cut, Math.Min(r.Width, r.Height) / 2);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(r.Left + c, r.Top), isFilled: true);
            g.LineTo(new Point(r.Right, r.Top));
            g.LineTo(new Point(r.Right, r.Bottom - c));
            g.LineTo(new Point(r.Right - c, r.Bottom));
            g.LineTo(new Point(r.Left, r.Bottom));
            g.LineTo(new Point(r.Left, r.Top + c));
            g.EndFigure(isClosed: true);
        }

        context.DrawGeometry(Fill, Stroke is null ? null : new Pen(Stroke, StrokeThickness), geometry);

        if (Accent is not null && c > 0)
        {
            var pen = new Pen(Accent, 2);
            context.DrawLine(pen, new Point(r.Left, r.Top + c), new Point(r.Left + c, r.Top));
            context.DrawLine(pen, new Point(r.Left + c, r.Top), new Point(r.Left + c + Math.Min(40, r.Width / 4), r.Top));
        }
    }
}
