using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Wavee.UI.WinUI.Shaders;
using Windows.Foundation;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.Cards;

public sealed partial class AnimatedHeroBackground : UserControl
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
    // Cached on the UI thread so OnDraw (Win2D render thread) doesn't have to read
    // the DP, which throws when accessed off the dispatcher.
    private float _clipRadius;

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

    public double ClipCornerRadius
    {
        get => (double)GetValue(ClipCornerRadiusProperty);
        set => SetValue(ClipCornerRadiusProperty, value);
    }

    public AnimatedHeroBackground()
    {
        InitializeComponent();
        _primary = ToFloat4(PrimaryColor);
        _accent = ToFloat4(AccentColor);
        _clipRadius = (float)ClipCornerRadius;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        PART_Canvas.Draw += OnDraw;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PART_Canvas.Paused = IsPaused;
        UpdateClip();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => PART_Canvas.Paused = true;

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
        if (d is not AnimatedHeroBackground self)
            return;
        if (self.PART_Canvas is { } canvas)
            canvas.Paused = (bool)e.NewValue || !self.IsLoaded;
    }

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not AnimatedHeroBackground self)
            return;
        var color = (Color)e.NewValue;
        if (e.Property == PrimaryColorProperty)
            self._primary = ToFloat4(color);
        else if (e.Property == AccentColorProperty)
            self._accent = ToFloat4(color);
    }

    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var width = (int)sender.ConvertDipsToPixels((float)sender.Size.Width, CanvasDpiRounding.Round);
        var height = (int)sender.ConvertDipsToPixels((float)sender.Size.Height, CanvasDpiRounding.Round);
        if (width <= 0 || height <= 0)
            return;

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

    private static float4 ToFloat4(Color c)
        => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
}
