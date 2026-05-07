using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace Wavee.UI.WinUI.Effects.Editorial;

/// <summary>
/// Bakes the home page's editorial-hero backdrop — adapted from
/// Klankhuis.Hero/Surfaces/BakedSurfaceCache.cs. Loads the featured cover,
/// runs a six-layer imperative paint (dark base, blurred source, radial
/// accent, diagonal accent, procedural noise, vignette) onto a
/// <see cref="CompositionDrawingSurface"/>, and hands back a reusable
/// <see cref="CompositionSurfaceBrush"/>.
///
/// One renderer per <c>EditorialHeroCard</c> instance; small LRU cache keyed
/// by <c>(uri, accent, size, theme)</c> so theme/size flips don't force a
/// full re-load of the source bitmap.
/// </summary>
public sealed class EditorialBackdropRenderer : IDisposable
{
    private readonly Compositor _compositor;
    private readonly Lazy<CanvasDevice> _device;
    private readonly Lazy<CompositionGraphicsDevice> _graphicsDevice;
    private readonly Dictionary<CacheKey, CacheEntry> _cache = new();
    private readonly object _gate = new();
    private const int MaxEntries = 4;
    private bool _disposed;

    public EditorialBackdropRenderer(Compositor compositor)
    {
        _compositor = compositor ?? throw new ArgumentNullException(nameof(compositor));
        _device = new Lazy<CanvasDevice>(() =>
        {
            var d = CanvasDevice.GetSharedDevice();
            d.DeviceLost += OnDeviceLost;
            return d;
        });
        _graphicsDevice = new Lazy<CompositionGraphicsDevice>(() =>
            CanvasComposition.CreateCompositionGraphicsDevice(_compositor, _device.Value));
    }

    /// <summary>
    /// Bakes (or returns the cached) backdrop surface brush for the given inputs.
    /// The returned brush is owned by the renderer — do not dispose it.
    /// </summary>
    public async Task<CompositionSurfaceBrush?> GetBrushAsync(
        Uri imageUri,
        Windows.UI.Color accent,
        SizeInt32 pixelSize,
        bool isDarkTheme,
        CancellationToken ct = default)
    {
        if (_disposed) return null;
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0) return null;

        var key = new CacheKey(imageUri, accent, pixelSize, isDarkTheme);

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                hit.Touched = Environment.TickCount64;
                return hit.Brush;
            }
        }

        // Load source bitmap off the UI thread.
        CanvasBitmap bitmap;
        try
        {
            var streamRef = RandomAccessStreamReference.CreateFromUri(imageUri);
            using var stream = await streamRef.OpenReadAsync().AsTask(ct);
            bitmap = await CanvasBitmap.LoadAsync(_device.Value, stream).AsTask(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();

        var size = new Size(pixelSize.Width, pixelSize.Height);
        var surface = _graphicsDevice.Value.CreateDrawingSurface(
            size,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        BakeImperative(surface, bitmap, accent, pixelSize, isDarkTheme);

        bitmap.Dispose();

        var brush = _compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.UniformToFill;

        lock (_gate)
        {
            // Re-check (another caller might have raced and won).
            if (_cache.TryGetValue(key, out var raceWinner))
            {
                surface.Dispose();
                raceWinner.Touched = Environment.TickCount64;
                return raceWinner.Brush;
            }

            _cache[key] = new CacheEntry(brush, surface, Environment.TickCount64);
            EvictIfNeeded();
        }

        return brush;
    }

    /// <summary>
    /// Drops every cached bake. Use on theme switch, DPI change, or device-lost
    /// recovery — the next <see cref="GetBrushAsync"/> call rebakes from scratch.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            foreach (var entry in _cache.Values)
            {
                entry.Brush.Dispose();
                entry.Surface.Dispose();
            }
            _cache.Clear();
        }
    }

    private void OnDeviceLost(CanvasDevice sender, object args) => Invalidate();

    private void EvictIfNeeded()
    {
        if (_cache.Count <= MaxEntries) return;

        CacheKey? oldestKey = null;
        var oldestTick = long.MaxValue;
        foreach (var (k, v) in _cache)
        {
            if (v.Touched < oldestTick)
            {
                oldestTick = v.Touched;
                oldestKey = k;
            }
        }
        if (oldestKey is { } key && _cache.Remove(key, out var entry))
        {
            entry.Brush.Dispose();
            entry.Surface.Dispose();
        }
    }

    /// <summary>
    /// Six imperative layers, painted once per item. Order is meaningful —
    /// each layer reads/writes the surface so the order shapes the result.
    /// See Klankhuis.Hero/Surfaces/BakedSurfaceCache.cs for the original.
    /// </summary>
    private void BakeImperative(
        CompositionDrawingSurface surface,
        CanvasBitmap source,
        Windows.UI.Color accent,
        SizeInt32 pixelSize,
        bool isDarkTheme)
    {
        var w = pixelSize.Width;
        var h = pixelSize.Height;

        using var session = CanvasComposition.CreateDrawingSession(surface);

        // 1. Dark base fill.
        session.Clear(Windows.UI.Color.FromArgb(255, 0x1A, 0x14, 0x20));

        // 2. Blurred + dimmed source. The image gives the bake its colour
        //    variety; without it the accent gradients alone read as a flat
        //    single-hue radial. Centred and overscanned (1.45×) so the blur
        //    halo doesn't reveal hard edges, blurred 60 DIPs, dimmed via
        //    Exposure to ~75% so it provides structure underneath the accent
        //    overlay rather than competing with it.
        using (var transform = new Transform2DEffect
        {
            Source = source,
            TransformMatrix = ComputeCenterTransform(source.Size, pixelSize),
            InterpolationMode = CanvasImageInterpolation.Linear,
        })
        using (var blurred = new GaussianBlurEffect
        {
            Source = transform,
            BlurAmount = 60f,
        })
        using (var dimmed = new ExposureEffect
        {
            Source = blurred,
            Exposure = -0.4f,
        })
        {
            session.DrawImage(dimmed);
        }

        // 3. Heavy accent radial gradient — 55% accent at the cover centre,
        //    decaying through 18% accent to 95% near-black at the edges.
        var nearBlack = Windows.UI.Color.FromArgb(255, 0x0F, 0x0C, 0x14);
        using (var radial = new CanvasRadialGradientBrush(_device.Value, new[]
        {
            new CanvasGradientStop { Position = 0f,    Color = WithAlpha(accent,    0.55f) },
            new CanvasGradientStop { Position = 0.45f, Color = WithAlpha(accent,    0.18f) },
            new CanvasGradientStop { Position = 1f,    Color = WithAlpha(nearBlack, 0.95f) },
        }))
        {
            radial.Center = new Vector2(w * 0.72f, h * 0.5f);
            radial.RadiusX = w * 0.85f;
            radial.RadiusY = h * 0.85f;
            session.FillRectangle(0, 0, w, h, radial);
        }

        // 4. Diagonal accent linear overlay.
        var bgCool = Windows.UI.Color.FromArgb(255, 0x14, 0x10, 0x1C);
        using (var linear = new CanvasLinearGradientBrush(_device.Value, new[]
        {
            new CanvasGradientStop { Position = 0f, Color = WithAlpha(accent, 0.40f) },
            new CanvasGradientStop { Position = 1f, Color = WithAlpha(bgCool, 0.95f) },
        }))
        {
            linear.StartPoint = new Vector2(0, 0);
            linear.EndPoint = new Vector2(w, h);
            session.FillRectangle(0, 0, w, h, linear);
        }

        // 5. Procedural noise — kills 8-bit colour banding visible across the
        //    heavy blur + smooth gradients. Theme-aware range matches the
        //    Microsoft Store paper §4.1.1.
        //
        //    CRITICAL: NoiseShader.Execute returns straight (unpremultiplied)
        //    alpha. Win2D effects assume premultiplied input; without an
        //    explicit PremultiplyEffect step the channels get a 20× boost.
        var (noiseMin, noiseMax) = isDarkTheme ? ((byte)0, (byte)255) : ((byte)128, (byte)255);
        using (var noise = new PixelShaderEffect<NoiseShader>())
        using (var premulNoise = new PremultiplyEffect { Source = noise })
        {
            noise.ConstantBuffer = new NoiseShader((byte)6, noiseMin, noiseMax); // ~2.4% alpha
            session.DrawImage(premulNoise, Vector2.Zero, new Rect(0, 0, w, h));
        }

        // 6. Soft vignette — pushes the corners down so the title block reads
        //    against a darker zone.
        using (var vignette = new CanvasRadialGradientBrush(_device.Value, new[]
        {
            new CanvasGradientStop { Position = 0.0f, Color = Windows.UI.Color.FromArgb(0, 0, 0, 0) },
            new CanvasGradientStop { Position = 0.7f, Color = Windows.UI.Color.FromArgb(0, 0, 0, 0) },
            new CanvasGradientStop { Position = 1.0f, Color = Windows.UI.Color.FromArgb(80, 0, 0, 0) },
        }))
        {
            vignette.Center = new Vector2(w * 0.5f, h * 0.5f);
            vignette.RadiusX = MathF.Max(w, h) * 0.75f;
            vignette.RadiusY = MathF.Max(w, h) * 0.75f;
            session.FillRectangle(0, 0, w, h, vignette);
        }
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color c, float a) =>
        Windows.UI.Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0f, 1f) * 255f), c.R, c.G, c.B);

    /// <summary>
    /// Affine transform that centres the source image inside the output rect
    /// (UniformToFill — bigger axis cropped — then translated). Overscan factor
    /// (1.45×) hides the blur halo at the edges.
    /// </summary>
    private static Matrix3x2 ComputeCenterTransform(Size source, SizeInt32 outputPx)
    {
        var sw = (float)source.Width;
        var sh = (float)source.Height;
        if (sw <= 0 || sh <= 0) return Matrix3x2.Identity;
        var ow = outputPx.Width;
        var oh = outputPx.Height;
        var scale = MathF.Max(ow / sw, oh / sh) * 1.45f;
        var dx = (ow - sw * scale) * 0.5f;
        var dy = (oh - sh * scale) * 0.5f;
        return Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(dx, dy);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Invalidate();
        if (_device.IsValueCreated)
        {
            _device.Value.DeviceLost -= OnDeviceLost;
        }
        if (_graphicsDevice.IsValueCreated)
        {
            _graphicsDevice.Value.Dispose();
        }
    }

    private readonly record struct CacheKey(
        Uri ImageUri,
        Windows.UI.Color Accent,
        SizeInt32 PixelSize,
        bool IsDarkTheme);

    private sealed class CacheEntry
    {
        public CompositionSurfaceBrush Brush { get; }
        public CompositionDrawingSurface Surface { get; }
        public long Touched { get; set; }

        public CacheEntry(CompositionSurfaceBrush brush, CompositionDrawingSurface surface, long touched)
        {
            Brush = brush;
            Surface = surface;
            Touched = touched;
        }
    }
}
