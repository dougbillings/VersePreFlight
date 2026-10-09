using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace VersePreflight.App.Theme;

/// <summary>The HUD palette. XAML uses these through x:Static so code and markup share one source.</summary>
public static class Hud
{
    public static readonly Color Bg0 = Color.Parse("#070B12");
    public static readonly Color Panel = Color.Parse("#D90C1622");
    public static readonly Color Line = Color.Parse("#24465A");
    public static readonly Color Accent = Color.Parse("#3FD8F2");
    public static readonly Color AccentBright = Color.Parse("#8BEBFA");
    public static readonly Color AccentDim = Color.Parse("#1D6F82");
    public static readonly Color Text = Color.Parse("#D7E7F0");
    public static readonly Color TextDim = Color.Parse("#7F97A8");
    public static readonly Color Good = Color.Parse("#4BE3A0");
    public static readonly Color Warn = Color.Parse("#FFB547");
    public static readonly Color Bad = Color.Parse("#FF5C6C");

    public static IBrush Bg0Brush { get; } = new ImmutableSolidColorBrush(Bg0);
    public static IBrush PanelBrush { get; } = new ImmutableSolidColorBrush(Panel);
    public static IBrush LineBrush { get; } = new ImmutableSolidColorBrush(Line);
    public static IBrush AccentBrush { get; } = new ImmutableSolidColorBrush(Accent);
    public static IBrush AccentBrightBrush { get; } = new ImmutableSolidColorBrush(AccentBright);
    public static IBrush AccentDimBrush { get; } = new ImmutableSolidColorBrush(AccentDim);
    public static IBrush TextBrush { get; } = new ImmutableSolidColorBrush(Text);
    public static IBrush TextDimBrush { get; } = new ImmutableSolidColorBrush(TextDim);
    public static IBrush GoodBrush { get; } = new ImmutableSolidColorBrush(Good);
    public static IBrush WarnBrush { get; } = new ImmutableSolidColorBrush(Warn);
    public static IBrush BadBrush { get; } = new ImmutableSolidColorBrush(Bad);

    public static IBrush ButtonBrush { get; } = new ImmutableSolidColorBrush(Color.Parse("#14243A"));
    public static IBrush ButtonHoverBrush { get; } = new ImmutableSolidColorBrush(Color.Parse("#1B3550"));
    public static IBrush OverlayBrush { get; } = new ImmutableSolidColorBrush(Color.Parse("#D903060B"));
    public static IBrush GridLineBrush { get; } = new ImmutableSolidColorBrush(Color.Parse("#0D3FD8F2"));

    public static FontFamily MonoFont { get; } = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
}
