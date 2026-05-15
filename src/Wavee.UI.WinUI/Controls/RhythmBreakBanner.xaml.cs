using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// Fluent rhythm-break banner used between dense music-data sections on the
/// artist page. Reworked from the original Spotify-style premium card to
/// match the rest of the Fluent card family (ContentCard / BaselineHomeCard):
/// 8&nbsp;px corners, <c>CardBackgroundFillColorDefaultBrush</c>, attached
/// composition shadow, and a subtle hover scale.
///
/// State-conditional accent layer: the left-edge accent stripe + dual-ring
/// pulse dot only render when <see cref="IsLive"/> is <c>true</c> (i.e. the
/// underlying eyebrow is "ON TOUR NOW"). Every other state shows calm Fluent
/// chrome only.
/// </summary>
public sealed partial class RhythmBreakBanner : UserControl
{
    public event EventHandler<RoutedEventArgs>? CardClick;

    // ── Dependency properties ───────────────────────────────────────────

    public static readonly DependencyProperty EyebrowProperty =
        DependencyProperty.Register(nameof(Eyebrow), typeof(string), typeof(RhythmBreakBanner),
            new PropertyMetadata("ON TOUR NOW", OnEyebrowChanged));

    public static readonly DependencyProperty HeadlineProperty =
        DependencyProperty.Register(nameof(Headline), typeof(string), typeof(RhythmBreakBanner),
            new PropertyMetadata(string.Empty, OnHeadlineChanged));

    public static readonly DependencyProperty SublineProperty =
        DependencyProperty.Register(nameof(Subline), typeof(string), typeof(RhythmBreakBanner),
            new PropertyMetadata(string.Empty, OnSublineChanged));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(RhythmBreakBanner),
            new PropertyMetadata(null, OnAccentBrushChanged));

    /// <summary>
    /// True when the underlying eyebrow is "ON TOUR NOW" — flips on the
    /// left-edge accent stripe and the pulse dot. False for all other
    /// eyebrow states (Upcoming tour / show / dates / Festival appearances).
    /// </summary>
    public static readonly DependencyProperty IsLiveProperty =
        DependencyProperty.Register(nameof(IsLive), typeof(bool), typeof(RhythmBreakBanner),
            new PropertyMetadata(false, OnIsLiveChanged));

    /// <summary>
    /// Segoe Fluent Icons glyph rendered in the icon column. The ViewModel
    /// picks the glyph per state (calendar for tour dates, microphone for a
    /// single show, party-popper for festival appearances).
    /// </summary>
    public static readonly DependencyProperty IconGlyphProperty =
        DependencyProperty.Register(nameof(IconGlyph), typeof(string), typeof(RhythmBreakBanner),
            new PropertyMetadata("", OnIconGlyphChanged));

    public string Eyebrow { get => (string)GetValue(EyebrowProperty); set => SetValue(EyebrowProperty, value); }
    public string Headline { get => (string)GetValue(HeadlineProperty); set => SetValue(HeadlineProperty, value); }
    public string Subline { get => (string)GetValue(SublineProperty); set => SetValue(SublineProperty, value); }
    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public bool IsLive { get => (bool)GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }
    public string IconGlyph { get => (string)GetValue(IconGlyphProperty); set => SetValue(IconGlyphProperty, value); }

    // Hover scale — matches BaselineHomeCard's pattern.
    private const float HoverScale = 1.02f;
    private const int HoverDurationMs = 160;
    private Visual? _rootVisual;

    public RhythmBreakBanner()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PointerEntered += OnPointerEnter;
        PointerExited += OnPointerLeave;
        PointerCanceled += OnPointerLeave;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (IsLive) StartPulse();
        EnsureRootVisualCenterPoint();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopPulse();

    // ── Hover scale ─────────────────────────────────────────────────────

    private void EnsureRootVisualCenterPoint()
    {
        if (_rootVisual is not null) return;
        if (CardRoot is null) return;

        _rootVisual = ElementCompositionPreview.GetElementVisual(CardRoot);
        // CenterPoint anchored to the geometric centre so the scale grows
        // symmetrically. Updated on SizeChanged in case the banner reflows.
        CardRoot.SizeChanged += (_, ev) =>
        {
            if (_rootVisual is null) return;
            _rootVisual.CenterPoint = new Vector3(
                (float)(ev.NewSize.Width / 2),
                (float)(ev.NewSize.Height / 2),
                0f);
        };
    }

    private void OnPointerEnter(object sender, PointerRoutedEventArgs e)
        => AnimateScale(HoverScale);

    private void OnPointerLeave(object sender, PointerRoutedEventArgs e)
        => AnimateScale(1.0f);

    private void AnimateScale(float target)
    {
        EnsureRootVisualCenterPoint();
        if (_rootVisual is null) return;

        var compositor = _rootVisual.Compositor;
        var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(1f, new Vector3(target, target, 1f));
        anim.Duration = TimeSpan.FromMilliseconds(HoverDurationMs);
        _rootVisual.StartAnimation("Scale", anim);
    }

    // ── Pulse animation (preserved from original) ───────────────────────

    private void StartPulse()
    {
        if (PulseRingOuter == null || PulseDot == null) return;

        var outerVisual = ElementCompositionPreview.GetElementVisual(PulseRingOuter);
        var innerVisual = ElementCompositionPreview.GetElementVisual(PulseDot);
        var compositor = outerVisual.Compositor;

        outerVisual.CenterPoint = new Vector3(7f, 7f, 0f);
        innerVisual.CenterPoint = new Vector3(4f, 4f, 0f);

        var outerScale = compositor.CreateVector3KeyFrameAnimation();
        outerScale.InsertKeyFrame(0f, new Vector3(0.6f, 0.6f, 0f));
        outerScale.InsertKeyFrame(1f, new Vector3(2.1f, 2.1f, 0f));
        outerScale.Duration = TimeSpan.FromMilliseconds(1800);
        outerScale.IterationBehavior = AnimationIterationBehavior.Forever;

        var outerOpacity = compositor.CreateScalarKeyFrameAnimation();
        outerOpacity.InsertKeyFrame(0f, 0.55f);
        outerOpacity.InsertKeyFrame(1f, 0f);
        outerOpacity.Duration = TimeSpan.FromMilliseconds(1800);
        outerOpacity.IterationBehavior = AnimationIterationBehavior.Forever;

        var innerScale = compositor.CreateVector3KeyFrameAnimation();
        innerScale.InsertKeyFrame(0f, new Vector3(1f, 1f, 0f));
        innerScale.InsertKeyFrame(0.5f, new Vector3(1.25f, 1.25f, 0f));
        innerScale.InsertKeyFrame(1f, new Vector3(1f, 1f, 0f));
        innerScale.Duration = TimeSpan.FromMilliseconds(1800);
        innerScale.IterationBehavior = AnimationIterationBehavior.Forever;

        outerVisual.StartAnimation("Scale", outerScale);
        outerVisual.StartAnimation("Opacity", outerOpacity);
        innerVisual.StartAnimation("Scale", innerScale);
    }

    private void StopPulse()
    {
        if (PulseRingOuter == null || PulseDot == null) return;
        try
        {
            var outerVisual = ElementCompositionPreview.GetElementVisual(PulseRingOuter);
            var innerVisual = ElementCompositionPreview.GetElementVisual(PulseDot);
            outerVisual.StopAnimation("Scale");
            outerVisual.StopAnimation("Opacity");
            innerVisual.StopAnimation("Scale");
        }
        catch
        {
            // Element torn down already.
        }
    }

    // ── DP callbacks ────────────────────────────────────────────────────

    private static void OnEyebrowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RhythmBreakBanner b) b.EyebrowText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnHeadlineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RhythmBreakBanner b) b.HeadlineText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnSublineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RhythmBreakBanner b)
        {
            var text = e.NewValue as string ?? string.Empty;
            b.SublineText.Text = text;
            b.SublineText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static void OnAccentBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RhythmBreakBanner b && e.NewValue is SolidColorBrush brush)
        {
            b.PulseDot.Fill = brush;
            b.PulseRingOuter.Fill = brush;
        }
    }

    private static void OnIsLiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RhythmBreakBanner b) return;
        var live = e.NewValue is true;
        b.AccentStripe.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        b.PulseDotHost.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        // Hide the static icon glyph when the pulse takes over the column —
        // otherwise the two stack on top of each other.
        b.StateIcon.Visibility = live ? Visibility.Collapsed : Visibility.Visible;

        if (b.IsLoaded)
        {
            if (live) b.StartPulse();
            else b.StopPulse();
        }
    }

    private static void OnIconGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RhythmBreakBanner b)
        {
            var glyph = e.NewValue as string;
            if (!string.IsNullOrEmpty(glyph))
                b.StateIcon.Glyph = glyph;
        }
    }

    private void CardButton_Click(object sender, RoutedEventArgs e) => CardClick?.Invoke(this, e);
}
