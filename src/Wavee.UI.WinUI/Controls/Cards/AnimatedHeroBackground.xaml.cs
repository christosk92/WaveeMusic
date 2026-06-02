using System;
using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Shaders;
using Wavee.UI.WinUI.Controls.PageHost;
using Windows.Foundation;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.Cards;

public sealed partial class AnimatedHeroBackground : UserControl, IHostVisibilityAware
{
    public static readonly DependencyProperty PrimaryColorProperty = DependencyProperty.Register(
        nameof(PrimaryColor),
        typeof(Color),
        typeof(AnimatedHeroBackground),
        new PropertyMetadata(Color.FromArgb(255, 90, 50, 160), OnColorChanged));

    public static readonly DependencyProperty AccentColorProperty = DependencyProperty.Register(
        nameof(AccentColor),
        typeof(Color),
        typeof(AnimatedHeroBackground),
        new PropertyMetadata(Color.FromArgb(255, 36, 198, 220), OnColorChanged));

    public static readonly DependencyProperty IsPausedProperty = DependencyProperty.Register(
        nameof(IsPaused),
        typeof(bool),
        typeof(AnimatedHeroBackground),
        new PropertyMetadata(false, OnIsPausedChanged));

    // Opt-in (default false → existing consumers snap exactly as before). When true, PrimaryColor /
    // AccentColor changes are eased toward on the render thread (per-frame lerp in OnDraw) for a
    // smooth palette morph — e.g. between swipe cards.
    public static readonly DependencyProperty AnimateColorTransitionsProperty = DependencyProperty.Register(
        nameof(AnimateColorTransitions),
        typeof(bool),
        typeof(AnimatedHeroBackground),
        new PropertyMetadata(false, OnAnimateColorTransitionsChanged));

    // Self-clip: a Win2D CanvasAnimatedControl swap chain is not reliably clipped by
    // a rounded ancestor (Border.CornerRadius / AttachedCardShadow CompositionMaskBrush
    // / parent CompositionGeometricClip). Apply the rounded clip directly on this
    // control's visual so the gradient never bleeds past the card's rounded edges.
    public static readonly DependencyProperty ClipCornerRadiusProperty = DependencyProperty.Register(
        nameof(ClipCornerRadius),
        typeof(double),
        typeof(AnimatedHeroBackground),
        new PropertyMetadata(0d, OnClipCornerRadiusChanged));

    private readonly PixelShaderEffect<MeshGradientShader> _effect = new();
    private float4 _primary;
    private float4 _accent;
    // Targets the on-screen colors ease toward when AnimateColorTransitions is on.
    private float4 _primaryTarget;
    private float4 _accentTarget;
    // Cached on the UI thread so OnDraw (render thread) never reads the DP (which throws off-thread).
    private bool _animateColors;
    // Cached on the UI thread so OnDraw (Win2D render thread) doesn't have to read
    // the DP, which throws when accessed off the dispatcher.
    private float _clipRadius;
    private bool _renderFailed;
    // Whether the host page is currently the visible active page. A collapsed
    // cached page keeps this control Loaded, but a Win2D CanvasAnimatedControl
    // keeps spinning its render loop while collapsed — so we pause it off-screen.
    // PageHost drives this on nav; OnLoaded seeds it from ancestor visibility.
    private bool _hostVisible = true;
    // Cached on the UI thread (construction) so the render-thread OnDraw failure path can
    // marshal back without touching DependencyObject.DispatcherQueue off-thread (which throws).
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    public Color PrimaryColor
    {
        get => (Color)GetValue(PrimaryColorProperty);
        set => SetValue(PrimaryColorProperty, value);
    }

    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public bool IsPaused
    {
        get => (bool)GetValue(IsPausedProperty);
        set => SetValue(IsPausedProperty, value);
    }

    public bool AnimateColorTransitions
    {
        get => (bool)GetValue(AnimateColorTransitionsProperty);
        set => SetValue(AnimateColorTransitionsProperty, value);
    }

    public double ClipCornerRadius
    {
        get => (double)GetValue(ClipCornerRadiusProperty);
        set => SetValue(ClipCornerRadiusProperty, value);
    }

    public AnimatedHeroBackground()
    {
        InitializeComponent();
        _primary = _primaryTarget = ToFloat4(PrimaryColor);
        _accent = _accentTarget = ToFloat4(AccentColor);
        _animateColors = AnimateColorTransitions;
        _clipRadius = (float)ClipCornerRadius;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        PART_Canvas.Draw += OnDraw;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // When a whole tab's ContentHost re-attaches, every cached page (active +
        // collapsed) fires Loaded at once. Seed visibility from the live tree so a
        // collapsed page's shader stays paused instead of all of them resuming.
        _hostVisible = !IsHostCollapsed();
        ApplyPauseState();
        UpdateClip();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => PART_Canvas.Paused = true;

    /// <summary>PageHost notifies us when the host page becomes visible / collapsed.</summary>
    public void OnHostVisibilityChanged(bool isVisible)
    {
        _hostVisible = isVisible;
        ApplyPauseState();
    }

    private void ApplyPauseState()
    {
        if (PART_Canvas is { } canvas)
            canvas.Paused = IsPaused || !IsLoaded || !_hostVisible;
    }

    private bool IsHostCollapsed()
    {
        DependencyObject? node = this;
        while (node is not null)
        {
            if (node is UIElement element && element.Visibility == Visibility.Collapsed)
                return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateClip();

    private static void OnClipCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnimatedHeroBackground self)
        {
            self._clipRadius = (float)(double)e.NewValue;
            self.UpdateClip();
        }
    }

    private void UpdateClip()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(this);
        var radius = (float)ClipCornerRadius;
        if (radius <= 0)
        {
            visual.Clip = null;
            return;
        }

        // RectangleClip with explicit per-corner radii is purpose-built for rounded
        // clipping; it's more pixel-precise than CompositionGeometricClip wrapping a
        // RoundedRectangleGeometry, which is what was bleeding at the rounded edges.
        var compositor = visual.Compositor;
        var clip = compositor.CreateRectangleClip();
        clip.Right = (float)ActualWidth;
        clip.Bottom = (float)ActualHeight;
        clip.TopLeftRadius = new Vector2(radius);
        clip.TopRightRadius = new Vector2(radius);
        clip.BottomLeftRadius = new Vector2(radius);
        clip.BottomRightRadius = new Vector2(radius);
        visual.Clip = clip;
    }

    private static void OnIsPausedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnimatedHeroBackground self)
            self.ApplyPauseState();
    }

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not AnimatedHeroBackground self)
            return;
        var f = ToFloat4((Color)e.NewValue);
        if (e.Property == PrimaryColorProperty)
        {
            self._primaryTarget = f;
            if (!self._animateColors) self._primary = f;
        }
        else if (e.Property == AccentColorProperty)
        {
            self._accentTarget = f;
            if (!self._animateColors) self._accent = f;
        }
    }

    private static void OnAnimateColorTransitionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AnimatedHeroBackground self)
            self._animateColors = (bool)e.NewValue;
    }

    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        if (_renderFailed)
            return;

        try
        {
            var width = (int)sender.ConvertDipsToPixels((float)sender.Size.Width, CanvasDpiRounding.Round);
            var height = (int)sender.ConvertDipsToPixels((float)sender.Size.Height, CanvasDpiRounding.Round);
            if (width <= 0 || height <= 0)
                return;

            if (_animateColors)
            {
                // Frame-rate-independent ease toward the target palette (~0.12s time constant).
                var dt = (float)args.Timing.ElapsedTime.TotalSeconds;
                var k = dt <= 0f ? 1f : 1f - MathF.Exp(-dt / 0.12f);
                _primary = Lerp(_primary, _primaryTarget, k);
                _accent = Lerp(_accent, _accentTarget, k);
            }

            _effect.ConstantBuffer = new MeshGradientShader(
                (float)args.Timing.TotalTime.TotalSeconds,
                new int2(width, height),
                _primary,
                _accent);

            // Clip the shader output to the rounded card shape inside Win2D itself.
            // SwapChainPanel content (CanvasAnimatedControl's swap chain) does NOT honour
            // composition clips applied to ancestors OR to its own visual — the swap chain
            // is presented separately by DComp on top of the WinUI tree. The only reliable
            // way to round the shader is to draw it inside a CanvasDrawingSession layer
            // bound by a rounded-rect CanvasGeometry, so the back buffer itself is rounded
            // (corners stay transparent from ClearColor=Transparent and blend with the
            // page background). _clipRadius is cached on the UI thread — reading the DP
            // from this render-thread callback would throw.
            var radius = _clipRadius;
            if (radius > 0)
            {
                var rect = new Rect(0, 0, sender.Size.Width, sender.Size.Height);
                using var clipGeometry = CanvasGeometry.CreateRoundedRectangle(args.DrawingSession, rect, radius, radius);
                using (args.DrawingSession.CreateLayer(1f, clipGeometry))
                {
                    args.DrawingSession.DrawImage(_effect);
                }
            }
            else
            {
                args.DrawingSession.DrawImage(_effect);
            }
        }
        catch (System.Exception ex)
        {
            // No usable graphics device / shader pipeline (GPU-less VM, RDP, driver fault).
            // Disable the animated layer and fall back to whatever the consumer paints behind
            // it, rather than letting the failure escape the Win2D render thread and crash the
            // process with a stowed exception. One-shot: stop the loop and collapse the surface.
            _renderFailed = true;
            System.Diagnostics.Debug.WriteLine($"[AnimatedHeroBackground] shader render failed, disabling: {ex}");
            _dispatcher?.TryEnqueue(() =>
            {
                if (PART_Canvas is { } canvas)
                {
                    canvas.Paused = true;
                    canvas.Visibility = Visibility.Collapsed;
                }
            });
        }
    }

    private static float4 ToFloat4(Color c)
        => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    private static float4 Lerp(float4 a, float4 b, float t)
        => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t), a.W + ((b.W - a.W) * t));
}