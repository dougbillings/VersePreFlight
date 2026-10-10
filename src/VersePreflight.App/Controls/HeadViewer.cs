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
/// Renders on the CPU at reduced resolution while moving; when the view settles, a full-quality frame
/// (supersampled on normal-density screens) is rendered in the background and swapped in.
/// </summary>
public sealed class HeadViewer : Control
{
    public static readonly StyledProperty<IReadOnlyList<MeshPart>?> PartsProperty =
        AvaloniaProperty.Register<HeadViewer, IReadOnlyList<MeshPart>?>(nameof(Parts));

    private const float DefaultYaw = 0.35f, DefaultPitch = 0.05f;
    private const int InteractivePixels = 300_000;      // fast frames while dragging
    private const int SupersampleBelowPixels = 700_000; // 2x supersample the settled frame up to this size

    private readonly HeadRenderer _renderer = new();
    private readonly HeadRenderer _settledRenderer = new(); // used only on the background thread
    private readonly object _settledLock = new();
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private WriteableBitmap? _bitmap;
    private uint[] _pixels = [];
    private float _yaw = DefaultYaw, _pitch = DefaultPitch, _zoom = 1;
    private Point? _dragFrom;
    private bool _interacting;
    private int _version;              // bumps whenever the view or the parts change
    private Settled? _settled;         // the last full-quality frame (used while it matches the view)
    private int _settledPending = -1;  // version of the full-quality frame being rendered

    private sealed record Settled(int Version, int Width, int Height, uint[] Pixels);

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
        _version++;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PartsProperty)
        {
            var parts = Parts ?? [];
            _renderer.SetParts(parts);
            lock (_settledLock) _settledRenderer.SetParts(parts);
            _version++;
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
        _version++;
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

        if (_settled is { } settled && settled.Version == _version && settled.Width == w && settled.Height == h)
        {
            Draw(context, settled.Pixels, w, h);
            return;
        }

        // A quick frame now, at a capped resolution
        var scale = MathF.Min(1, MathF.Sqrt(InteractivePixels / (float)(w * h)));
        int rw = Math.Max(1, (int)(w * scale)), rh = Math.Max(1, (int)(h * scale));
        if (_pixels.Length < rw * rh) _pixels = new uint[rw * rh];
        _renderer.Render(_pixels, rw, rh, _yaw, _pitch, _zoom, supersample: rw / (float)w);
        Draw(context, _pixels, rw, rh);

        // Then the full-quality frame in the background, once the view has settled
        if (!_interacting && _settledPending != _version) RenderSettled(w, h);
    }

    private void RenderSettled(int w, int h)
    {
        var version = _settledPending = _version;
        var (yaw, pitch, zoom) = (_yaw, _pitch, _zoom);
        var supersample = w * h <= SupersampleBelowPixels ? 2 : 1;
        Task.Run(() =>
        {
            uint[] pixels;
            lock (_settledLock)
            {
                if (version != Volatile.Read(ref _version)) return; // the view has moved on
                var full = new uint[w * supersample * h * supersample];
                _settledRenderer.Render(full, w * supersample, h * supersample, yaw, pitch, zoom, supersample);
                pixels = supersample == 1 ? full : Downsample(full, w, h);
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (version != _version) return;
                _settled = new Settled(version, w, h, pixels);
                InvalidateVisual();
            });
        });
    }

    private void Draw(DrawingContext context, uint[] pixels, int w, int h)
    {
        if (_bitmap is null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
        using (var fb = _bitmap.Lock())
        {
            for (var y = 0; y < h; y++)
                Marshal.Copy((int[])(object)pixels, y * w, fb.Address + y * fb.RowBytes, w);
        }
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            context.DrawImage(_bitmap, new Rect(0, 0, w, h), new Rect(Bounds.Size));
    }

    // 2x2 box filter with premultiplied alpha, for smooth edges
    private static uint[] Downsample(uint[] src, int w, int h)
    {
        var dst = new uint[w * h];
        var sw = w * 2;
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                uint a = 0, r = 0, g = 0, b = 0;
                foreach (var s in (ReadOnlySpan<uint>)[src[(2 * y) * sw + 2 * x], src[(2 * y) * sw + 2 * x + 1], src[(2 * y + 1) * sw + 2 * x], src[(2 * y + 1) * sw + 2 * x + 1]])
                {
                    a += s >> 24; r += (s >> 16) & 0xFF; g += (s >> 8) & 0xFF; b += s & 0xFF;
                }
                dst[y * w + x] = (a / 4) << 24 | (r / 4) << 16 | (g / 4) << 8 | (b / 4);
            }
        });
        return dst;
    }
}
