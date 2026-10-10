using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VersePreflight.App.Rendering;

namespace VersePreflight.App.Controls;

/// <summary>
/// Interactive head viewer: drag to rotate, wheel or pinch to zoom, double-tap to reset.
/// Renders on the CPU at screen resolution while moving, then 2x supersampled when idle.
/// </summary>
public sealed class HeadViewer : Control
{
    public static readonly StyledProperty<IReadOnlyList<MeshPart>?> PartsProperty =
        AvaloniaProperty.Register<HeadViewer, IReadOnlyList<MeshPart>?>(nameof(Parts));

    private const float DefaultYaw = 0.35f, DefaultPitch = 0.05f;

    private readonly HeadRenderer _renderer = new();
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private WriteableBitmap? _bitmap;
    private uint[] _pixels = [];
    private uint[] _supersampled = [];
    private float[] _depth = [];
    private float _yaw = DefaultYaw, _pitch = DefaultPitch, _zoom = 1;
    private Point? _dragFrom;
    private bool _interacting;

    public HeadViewer()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        GestureRecognizers.Add(new PinchGestureRecognizer());
        AddHandler(InputElement.PinchEvent, (_, e) => { _zoom = Math.Clamp(_zoom * (float)Math.Pow(e.Scale, 0.05), 0.6f, 3.5f); Changed(); });
        DoubleTapped += (_, _) => ResetView();
        _idleTimer.Tick += (_, _) => { _idleTimer.Stop(); _interacting = false; InvalidateVisual(); };
    }

    /// <summary>What to draw; the first part (the head) frames the camera.</summary>
    public IReadOnlyList<MeshPart>? Parts { get => GetValue(PartsProperty); set => SetValue(PartsProperty, value); }

    public void ResetView()
    {
        (_yaw, _pitch, _zoom) = (DefaultYaw, DefaultPitch, 1);
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PartsProperty)
        {
            _renderer.SetParts(Parts ?? []);
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from) return;
        var p = e.GetPosition(this);
        _yaw += (float)(p.X - from.X) * 0.008f;
        _pitch = Math.Clamp(_pitch + (float)(p.Y - from.Y) * 0.006f, -1.1f, 1.1f);
        _dragFrom = p;
        Changed();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _zoom = Math.Clamp(_zoom * (float)Math.Pow(1.12, e.Delta.Y), 0.6f, 3.5f);
        Changed();
        e.Handled = true;
    }

    private void Changed()
    {
        _interacting = true;
        _idleTimer.Stop();
        _idleTimer.Start();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!_renderer.HasMesh || Bounds.Width < 2 || Bounds.Height < 2) return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var w = Math.Max(1, (int)(Bounds.Width * scaling));
        var h = Math.Max(1, (int)(Bounds.Height * scaling));
        var ss = _interacting ? 1 : 2;

        EnsureBuffers(w, h, ss);
        if (ss == 1)
        {
            _renderer.Render(_pixels, _depth, w, h, _yaw, _pitch, _zoom);
        }
        else
        {
            _renderer.Render(_supersampled, _depth, w * 2, h * 2, _yaw, _pitch, _zoom);
            Downsample(_supersampled, _pixels, w, h);
        }

        using (var fb = _bitmap!.Lock())
        {
            for (var y = 0; y < h; y++)
                Marshal.Copy((int[])(object)_pixels, y * w, fb.Address + y * fb.RowBytes, w);
        }
        context.DrawImage(_bitmap, new Rect(0, 0, w, h), new Rect(Bounds.Size));
    }

    private void EnsureBuffers(int w, int h, int ss)
    {
        if (_bitmap is null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            _pixels = new uint[w * h];
        }
        var needed = w * h * ss * ss;
        if (_depth.Length < needed) _depth = new float[needed];
        if (ss > 1 && _supersampled.Length < needed) _supersampled = new uint[needed];
    }

    // 2x2 box filter with premultiplied alpha, for smooth edges when idle
    private static void Downsample(uint[] src, uint[] dst, int w, int h)
    {
        var sw = w * 2;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                uint a = 0, r = 0, g = 0, b = 0;
                foreach (var s in (ReadOnlySpan<uint>)[src[(2 * y) * sw + 2 * x], src[(2 * y) * sw + 2 * x + 1], src[(2 * y + 1) * sw + 2 * x], src[(2 * y + 1) * sw + 2 * x + 1]])
                {
                    a += s >> 24; r += (s >> 16) & 0xFF; g += (s >> 8) & 0xFF; b += s & 0xFF;
                }
                dst[y * w + x] = (a / 4) << 24 | (r / 4) << 16 | (g / 4) << 8 | (b / 4);
            }
    }
}
