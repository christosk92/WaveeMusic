using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Playback;

/// <summary>
/// Composition-driven horizontal progress bar — replaces a bound Slider in the
/// playback chrome. The fill animates on the composition thread via per-segment
/// linear ScalarKeyFrameAnimations on Visual.Scale.X, while the view model
/// updates the anchor position on service events, seeks, track changes, and a
/// coarse 1 Hz interpolation tick.
///
/// When <see cref="Segments"/> is null or empty, the bar renders a single
/// continuous fill (legacy behaviour). When <see cref="Segments"/> has items,
/// the bar renders one track + fill pair per chapter, separated by 3 px gaps,
/// each independently filling 0 → 100 % of its own range as playback moves
/// through it. Past chapters render fully filled, future chapters empty.
///
/// Bind <see cref="PositionMs"/> to <c>PlayerBarViewModel.AnchorPositionMs</c>;
/// bind <see cref="Segments"/> to <c>PlayerBarViewModel.Chapters</c>.
/// </summary>
public sealed class CompositionProgressBar : UserControl
{
    private const double TrackHeight = 3.0;
    private const double TrackHeightHover = 4.0;
    private const double CornerRadiusValue = 1.5;
    private const double SegmentSpacing = 3.0;
    private const double ThumbSize = 12.0;
    private const double ThumbFadeMs = 120.0;
    /// <summary>Pointer-movement threshold above which a press is treated as a
    /// drag (continuous seek) rather than a click (chapter-start snap, when in
    /// segmented mode).</summary>
    private const double ClickSnapThresholdPx = 4.0;

    private readonly Grid _root;
    private readonly Grid _segmentsGrid;
    private readonly Border _thumbBorder;
    private Visual? _thumbVisual;
    private Compositor? _compositor;
    private bool _templateApplied;

    // Per-segment visuals — one entry per rendered range.
    private readonly List<Border> _trackBorders = new();
    private readonly List<Border> _fillBorders = new();
    private readonly List<Visual> _fillVisuals = new();
    /// <summary>Each segment's [startMs, stopMs) over the global timeline. For the
    /// no-chapters single-bar case this holds one (0, +∞) range so Resync can
    /// detect "single-segment" by checking <c>stopMs == +∞</c>.</summary>
    private readonly List<(double startMs, double stopMs)> _segmentRanges = new();

    private bool _isDragging;
    private bool _isHovering;
    private double _lastDragRatio;
    /// <summary>X position (in control-local coordinates) where the most recent
    /// pointer press landed. Used to distinguish a click (≤ threshold movement,
    /// chapter-snap on release) from a drag (> threshold, exact seek on release).</summary>
    private double _pressX;
    private bool _hasDraggedPastThreshold;

    // ── Chapter hover tooltip ─────────────────────────────────────────────
    // Custom Popup-based tooltip — replaces ToolTipService which has a fixed
    // ~500 ms show delay + low-contrast platform chrome. This one shows
    // instantly, follows the pointer, and reads against any palette wash.
    private readonly Popup _chapterTooltipPopup;
    private readonly Border _chapterTooltipBorder;
    private readonly TextBlock _chapterTooltipTitle;
    private readonly TextBlock _chapterTooltipMeta;
    private EpisodeChapterVm? _activeTooltipChapter;

    public CompositionProgressBar()
    {
        _root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };

        _segmentsGrid = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        _thumbBorder = new Border
        {
            Width = ThumbSize,
            Height = ThumbSize,
            CornerRadius = new CornerRadius(ThumbSize / 2),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush?)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Opacity = 0,
            IsHitTestVisible = false,
        };

        _root.Children.Add(_segmentsGrid);
        _root.Children.Add(_thumbBorder);
        Content = _root;

        Height = 16;
        MinHeight = 16;
        IsTabStop = true;

        // Build the chapter tooltip popup once. Reused across all segments;
        // content + position are mutated on hover. IsHitTestVisible=false so
        // the popup never steals pointer events from the bar underneath.
        // Theme-aware: brushes resolve from Application resources so light/dark
        // theme switches re-tint the tooltip automatically (RequestedTheme=Default
        // makes the popup inherit from XamlRoot at attach time).
        _chapterTooltipTitle = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveBrush("TextFillColorPrimaryBrush", Colors.White),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _chapterTooltipMeta = new TextBlock
        {
            FontSize = 11,
            Foreground = ResolveBrush("TextFillColorSecondaryBrush", Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
        };
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(_chapterTooltipTitle);
        stack.Children.Add(_chapterTooltipMeta);
        _chapterTooltipBorder = new Border
        {
            // ToolTipBackground / ToolTipBorderBrush are the theme-aware
            // surfaces the platform ToolTip control itself uses — gives us
            // matching chrome with no per-theme guesswork.
            Background = ResolveBrush("ToolTipBackground", Color.FromArgb(0xF2, 0x1A, 0x1A, 0x1A)),
            BorderBrush = ResolveBrush("ToolTipBorderBrush", Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            MinWidth = 80,
            MaxWidth = 280,
            Child = stack,
            IsHitTestVisible = false,
            RequestedTheme = ElementTheme.Default,
        };
        _chapterTooltipPopup = new Popup
        {
            Child = _chapterTooltipBorder,
            IsHitTestVisible = false,
            ShouldConstrainToRootBounds = true,
        };

        // Build the default single-bar visual so the control has something to
        // render before Loaded — matches the legacy behaviour.
        RebuildSegments();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Resync();
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
    }

    // ── Public properties ────────────────────────────────────────────────

    public double PositionMs
    {
        get => (double)GetValue(PositionMsProperty);
        set => SetValue(PositionMsProperty, value);
    }
    public static readonly DependencyProperty PositionMsProperty =
        DependencyProperty.Register(nameof(PositionMs), typeof(double), typeof(CompositionProgressBar),
            new PropertyMetadata(0.0, OnAnimationInputChanged));

    public double DurationMs
    {
        get => (double)GetValue(DurationMsProperty);
        set => SetValue(DurationMsProperty, value);
    }
    public static readonly DependencyProperty DurationMsProperty =
        DependencyProperty.Register(nameof(DurationMs), typeof(double), typeof(CompositionProgressBar),
            new PropertyMetadata(0.0, OnAnimationInputChanged));

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }
    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(CompositionProgressBar),
            new PropertyMetadata(false, OnAnimationInputChanged));

    public bool IsBuffering
    {
        get => (bool)GetValue(IsBufferingProperty);
        set => SetValue(IsBufferingProperty, value);
    }
    public static readonly DependencyProperty IsBufferingProperty =
        DependencyProperty.Register(nameof(IsBuffering), typeof(bool), typeof(CompositionProgressBar),
            new PropertyMetadata(false, OnAnimationInputChanged));

    /// <summary>
    /// Optional chapter list. When null or empty the bar renders a single
    /// continuous fill; when populated it splits into N visually-separate
    /// segments sized by per-chapter duration. Bound to
    /// <c>PlayerBarViewModel.Chapters</c>.
    /// </summary>
    public IReadOnlyList<EpisodeChapterVm>? Segments
    {
        get => (IReadOnlyList<EpisodeChapterVm>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }
    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.Register(nameof(Segments), typeof(IReadOnlyList<EpisodeChapterVm>), typeof(CompositionProgressBar),
            new PropertyMetadata(null, OnSegmentsChanged));

    // ── Events ───────────────────────────────────────────────────────────

    public event EventHandler? SeekStarted;
    public event EventHandler<double>? SeekCommitted;

    // ── Animation core ───────────────────────────────────────────────────

    private static void OnAnimationInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CompositionProgressBar bar) bar.Resync();
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CompositionProgressBar bar)
        {
            bar.RebuildSegments();
            bar.Resync();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _thumbVisual = ElementCompositionPreview.GetElementVisual(_thumbBorder);
        _compositor = _thumbVisual.Compositor;

        var opacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.Target = nameof(Visual.Opacity);
        opacityAnim.InsertExpressionKeyFrame(1f, "this.FinalValue");
        opacityAnim.Duration = TimeSpan.FromMilliseconds(ThumbFadeMs);
        var implicitCollection = _compositor.CreateImplicitAnimationCollection();
        implicitCollection[nameof(Visual.Opacity)] = opacityAnim;
        _thumbVisual.ImplicitAnimations = implicitCollection;

        BindFillVisuals();
        _templateApplied = true;
        Resync();
    }

    /// <summary>
    /// Rebuilds the segments grid — column definitions sized proportionally to
    /// chapter durations, plus track + fill borders per segment. Called from
    /// the constructor (initial single-bar) and from <see cref="OnSegmentsChanged"/>.
    /// </summary>
    private void RebuildSegments()
    {
        // Stop any animations on the existing fills before clearing.
        foreach (var v in _fillVisuals)
            v.StopAnimation("Scale.X");

        _trackBorders.Clear();
        _fillBorders.Clear();
        _fillVisuals.Clear();
        _segmentRanges.Clear();
        _segmentsGrid.Children.Clear();
        _segmentsGrid.ColumnDefinitions.Clear();

        var segments = Segments;
        if (segments is null || segments.Count == 0)
        {
            // Single-bar fallback. ColumnSpacing=0 since there's only one column.
            _segmentsGrid.ColumnSpacing = 0;
            _segmentsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _segmentRanges.Add((0.0, double.PositiveInfinity));
            AddSegmentVisuals(0, chapter: null);
        }
        else
        {
            _segmentsGrid.ColumnSpacing = SegmentSpacing;
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                // Column weight = chapter duration (ms). Star sizing distributes
                // ActualWidth proportionally across all chapters, so a 5-min
                // chapter gets exactly 5× the column width of a 1-min chapter.
                var weight = Math.Max(1, seg.StopMilliseconds - seg.StartMilliseconds);
                _segmentsGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(weight, GridUnitType.Star)
                });
                _segmentRanges.Add((seg.StartMilliseconds, seg.StopMilliseconds));
                AddSegmentVisuals(i, chapter: seg);
            }
        }

        // Re-bind the freshly created Borders to composition Visuals if we're
        // already past Loaded. RebuildSegments runs in the constructor (before
        // Loaded) for the default case, so we guard.
        if (_templateApplied) BindFillVisuals();
    }

    // Lifted-white track — sits on dark Mica or palette wash and stays visible
    // regardless of show colour. Theme-aware ControlFillColorTertiaryBrush
    // collapses against saturated palettes (we saw this on a red/orange wash).
    private static readonly Brush TrackBrush =
        new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    private void AddSegmentVisuals(int columnIndex, EpisodeChapterVm? chapter)
    {
        var height = _isHovering || _isDragging ? TrackHeightHover : TrackHeight;

        var track = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(CornerRadiusValue),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = TrackBrush,
        };
        var fill = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(CornerRadiusValue),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = (Brush?)Application.Current.Resources["AccentFillColorDefaultBrush"],
        };

        Grid.SetColumn(track, columnIndex);
        Grid.SetColumn(fill, columnIndex);
        _segmentsGrid.Children.Add(track);
        _segmentsGrid.Children.Add(fill);
        _trackBorders.Add(track);
        _fillBorders.Add(fill);
    }

    private void BindFillVisuals()
    {
        _fillVisuals.Clear();
        foreach (var fill in _fillBorders)
        {
            var visual = ElementCompositionPreview.GetElementVisual(fill);
            // Anchor at left so X-scaling grows rightward.
            visual.CenterPoint = new Vector3(0f, 0f, 0f);
            _fillVisuals.Add(visual);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopAllFillAnimations();
        _templateApplied = false;
    }

    private void Resync()
    {
        if (!_templateApplied || _compositor is null || _segmentRanges.Count == 0) return;
        if (ActualWidth <= 0) return;

        UpdateThumbOffset();

        if (_isDragging) return;

        for (var i = 0; i < _fillVisuals.Count; i++)
        {
            var visual = _fillVisuals[i];
            var (segStart, segStop) = _segmentRanges[i];
            visual.StopAnimation("Scale.X");

            if (double.IsPositiveInfinity(segStop))
            {
                // Single-bar (no chapters) path — keep legacy behaviour.
                ApplyGlobalFill(visual);
                continue;
            }

            ApplySegmentFill(visual, segStart, segStop);
        }
    }

    private void ApplyGlobalFill(Visual visual)
    {
        if (_compositor is null) return;

        var ratio = (float)(DurationMs > 0
            ? Math.Clamp(PositionMs / DurationMs, 0.0, 1.0)
            : 0.0);

        if (!IsPlaying || IsBuffering || DurationMs <= 0)
        {
            visual.Scale = new Vector3(ratio, 1f, 1f);
            return;
        }

        var remainingMs = Math.Max(0, DurationMs - PositionMs);
        if (remainingMs <= 0)
        {
            visual.Scale = new Vector3(1f, 1f, 1f);
            return;
        }

        visual.Scale = new Vector3(ratio, 1f, 1f);

        var anim = _compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, ratio);
        anim.InsertKeyFrame(1f, 1f, _compositor.CreateLinearEasingFunction());
        anim.Duration = TimeSpan.FromMilliseconds(remainingMs);
        anim.IterationBehavior = AnimationIterationBehavior.Count;
        anim.IterationCount = 1;
        anim.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;

        visual.StartAnimation("Scale.X", anim);
    }

    private void ApplySegmentFill(Visual visual, double segStartMs, double segStopMs)
    {
        if (_compositor is null) return;

        var segMs = Math.Max(1, segStopMs - segStartMs);

        // Past segment — fully filled.
        if (PositionMs >= segStopMs)
        {
            visual.Scale = new Vector3(1f, 1f, 1f);
            return;
        }
        // Future segment — empty.
        if (PositionMs <= segStartMs)
        {
            visual.Scale = new Vector3(0f, 1f, 1f);
            return;
        }

        // Active segment — fill from current in-segment ratio to 1 over the
        // segment's remaining duration. When the position crosses into the next
        // segment, Resync fires again (driven by the 1 Hz position interpolation
        // tick in PlayerBarViewModel) and the next segment takes over animating.
        var inSegmentRatio = (float)Math.Clamp((PositionMs - segStartMs) / segMs, 0.0, 1.0);

        if (!IsPlaying || IsBuffering)
        {
            visual.Scale = new Vector3(inSegmentRatio, 1f, 1f);
            return;
        }

        var remainingInSegment = Math.Max(0, segStopMs - PositionMs);
        if (remainingInSegment <= 0)
        {
            visual.Scale = new Vector3(1f, 1f, 1f);
            return;
        }

        visual.Scale = new Vector3(inSegmentRatio, 1f, 1f);

        var anim = _compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, inSegmentRatio);
        anim.InsertKeyFrame(1f, 1f, _compositor.CreateLinearEasingFunction());
        anim.Duration = TimeSpan.FromMilliseconds(remainingInSegment);
        anim.IterationBehavior = AnimationIterationBehavior.Count;
        anim.IterationCount = 1;
        anim.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;

        visual.StartAnimation("Scale.X", anim);
    }

    private void UpdateThumbOffset()
    {
        if (_thumbVisual is null) return;
        var width = (float)ActualWidth;
        if (width <= 0) return;

        var ratio = DurationMs > 0
            ? Math.Clamp(PositionMs / DurationMs, 0.0, 1.0)
            : 0.0;
        // Thumb offset uses the global ratio over ActualWidth — close enough
        // even with segment gaps; the ~3 px gap accumulation across 7 chapters
        // is < 3 % of typical bar width and visually unnoticeable.
        var x = (float)(ratio * width - ThumbSize / 2.0);
        _thumbVisual.Offset = new Vector3(x, 0f, 0f);
    }

    private void StopAllFillAnimations()
    {
        foreach (var v in _fillVisuals)
            v.StopAnimation("Scale.X");
    }

    // ── Pointer / drag handling ──────────────────────────────────────────

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = true;
        SetBarHeight(TrackHeightHover);
        if (_thumbVisual != null) _thumbVisual.Opacity = 1f;
        UpdateChapterTooltip(e);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = false;
        HideChapterTooltip();
        // Keep the bar/thumb expanded while a drag is in flight even if the
        // pointer leaves the bar's bounds — matches Spotify/Apple Music seek
        // ergonomics. Final cleanup runs in ReleaseDragWithCommit.
        if (_isDragging) return;
        SetBarHeight(TrackHeight);
        if (_thumbVisual != null) _thumbVisual.Opacity = 0f;
    }

    private void SetBarHeight(double height)
    {
        foreach (var t in _trackBorders) t.Height = height;
        foreach (var f in _fillBorders) f.Height = height;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = true;
        _pressX = e.GetCurrentPoint(this).Position.X;
        _hasDraggedPastThreshold = false;
        StopAllFillAnimations();
        SeekStarted?.Invoke(this, EventArgs.Empty);
        UpdateDragRatio(e);
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Tooltip tracks the cursor whenever the pointer is over the bar,
        // not only during drag — Pointer{Entered,Exited} bracket the show/hide.
        UpdateChapterTooltip(e);

        if (!_isDragging) return;
        var x = e.GetCurrentPoint(this).Position.X;
        if (Math.Abs(x - _pressX) > ClickSnapThresholdPx)
            _hasDraggedPastThreshold = true;
        UpdateDragRatio(e);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        UpdateDragRatio(e);
        ReleaseDragWithCommit(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        ReleaseDragWithCommit(e.Pointer);
    }

    private void UpdateDragRatio(PointerRoutedEventArgs e)
    {
        var width = ActualWidth;
        if (width <= 0) return;

        var x = e.GetCurrentPoint(this).Position.X;
        _lastDragRatio = Math.Clamp(x / width, 0.0, 1.0);

        // Drive each segment's fill scale based on a synthetic "drag PositionMs"
        // so the visual previews where the seek lands. Past segments → 1, future
        // → 0, the segment containing the drag head shows its in-segment fill.
        var dragPositionMs = _lastDragRatio * DurationMs;
        for (var i = 0; i < _fillVisuals.Count; i++)
        {
            var visual = _fillVisuals[i];
            var (segStart, segStop) = _segmentRanges[i];

            if (double.IsPositiveInfinity(segStop))
            {
                visual.Scale = new Vector3((float)_lastDragRatio, 1f, 1f);
                continue;
            }

            if (dragPositionMs >= segStop)
                visual.Scale = new Vector3(1f, 1f, 1f);
            else if (dragPositionMs <= segStart)
                visual.Scale = new Vector3(0f, 1f, 1f);
            else
            {
                var segMs = Math.Max(1, segStop - segStart);
                var ratio = (float)Math.Clamp((dragPositionMs - segStart) / segMs, 0.0, 1.0);
                visual.Scale = new Vector3(ratio, 1f, 1f);
            }
        }

        // Thumb tracks the pointer directly during drag.
        if (_thumbVisual != null)
        {
            var thumbX = (float)(_lastDragRatio * width - ThumbSize / 2.0);
            _thumbVisual.Offset = new Vector3(thumbX, 0f, 0f);
        }
    }

    private void ReleaseDragWithCommit(Pointer pointer)
    {
        _isDragging = false;
        ReleasePointerCapture(pointer);

        var newPositionMs = ResolveSeekTargetMs();
        SeekCommitted?.Invoke(this, newPositionMs);

        // Restore the resting state IF the pointer isn't still over the bar.
        // If it is, hover state stays — user can drag again immediately.
        if (!_isHovering)
        {
            SetBarHeight(TrackHeight);
            if (_thumbVisual != null) _thumbVisual.Opacity = 0f;
        }
        // Resync re-fires when the VM re-anchors after Seek round-trips.
    }

    // ── Chapter hover tooltip ────────────────────────────────────────────

    private void UpdateChapterTooltip(PointerRoutedEventArgs e)
    {
        var segments = Segments;
        if (segments is null || segments.Count == 0)
        {
            HideChapterTooltip();
            return;
        }

        var width = ActualWidth;
        if (width <= 0 || DurationMs <= 0)
        {
            HideChapterTooltip();
            return;
        }

        var pointerX = e.GetCurrentPoint(this).Position.X;
        var ratio = Math.Clamp(pointerX / width, 0.0, 1.0);
        var pointerMs = ratio * DurationMs;

        EpisodeChapterVm? hit = null;
        foreach (var chapter in segments)
        {
            if (pointerMs >= chapter.StartMilliseconds && pointerMs < chapter.StopMilliseconds)
            {
                hit = chapter;
                break;
            }
        }
        // Past the last chapter's stop (or rounding gap) → snap to nearest by ms.
        if (hit is null)
        {
            for (var i = segments.Count - 1; i >= 0; i--)
            {
                if (pointerMs >= segments[i].StartMilliseconds) { hit = segments[i]; break; }
            }
            hit ??= segments[0];
        }

        if (XamlRoot is null)
        {
            HideChapterTooltip();
            return;
        }

        // Update content only when the chapter under the cursor changes —
        // re-binding TextBlock.Text forces an unnecessary layout pass otherwise.
        if (!ReferenceEquals(_activeTooltipChapter, hit))
        {
            _activeTooltipChapter = hit;
            _chapterTooltipTitle.Text = hit.Title ?? "";
            _chapterTooltipMeta.Text = hit.TimeRange ?? "";
            // Force a measure so HorizontalOffset can centre the popup on the
            // pointer using the freshly-measured size.
            _chapterTooltipBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        if (!_chapterTooltipPopup.IsOpen)
        {
            _chapterTooltipPopup.XamlRoot = XamlRoot;
            _chapterTooltipPopup.IsOpen = true;
        }

        // Position: centred horizontally on the pointer, sitting just above the
        // bar. Convert from control-local coordinates to XamlRoot coordinates
        // because Popup offsets are root-absolute when XamlRoot is set.
        var rootContent = XamlRoot.Content as UIElement;
        if (rootContent is null) return;
        var transform = TransformToVisual(rootContent);
        var origin = transform.TransformPoint(new Point(pointerX, 0));
        var size = _chapterTooltipBorder.DesiredSize;
        var popupWidth = size.Width > 0 ? size.Width : _chapterTooltipBorder.ActualWidth;
        var popupHeight = size.Height > 0 ? size.Height : _chapterTooltipBorder.ActualHeight;

        _chapterTooltipPopup.HorizontalOffset = origin.X - popupWidth / 2.0;
        _chapterTooltipPopup.VerticalOffset = origin.Y - popupHeight - 8.0;
    }

    private void HideChapterTooltip()
    {
        if (_chapterTooltipPopup.IsOpen) _chapterTooltipPopup.IsOpen = false;
        _activeTooltipChapter = null;
    }

    private static Brush ResolveBrush(string resourceKey, Color fallback)
    {
        if (Application.Current?.Resources is { } res
            && res.TryGetValue(resourceKey, out var value)
            && value is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }

    /// <summary>
    /// Picks the seek target on release. In segmented mode (chapters present),
    /// a click — pointer movement at or below <see cref="ClickSnapThresholdPx"/>
    /// — snaps to the start of whichever chapter the press landed on, so users
    /// can jump to a chapter without scrubbing. A real drag still commits the
    /// exact pointer position. In single-bar mode the press always commits its
    /// exact ratio (existing music-bar behaviour).
    /// </summary>
    private double ResolveSeekTargetMs()
    {
        var dragMs = _lastDragRatio * DurationMs;
        if (_hasDraggedPastThreshold) return dragMs;

        // No real drag and no chapter context → fall back to exact seek (single-bar
        // or chapter list still loading).
        if (_segmentRanges.Count == 0 || double.IsPositiveInfinity(_segmentRanges[0].stopMs))
            return dragMs;

        var width = ActualWidth;
        if (width <= 0 || DurationMs <= 0) return dragMs;

        var pressMs = Math.Clamp(_pressX / width, 0.0, 1.0) * DurationMs;
        foreach (var (start, stop) in _segmentRanges)
        {
            if (pressMs >= start && pressMs < stop)
                return start;
        }
        // Pressed past the last chapter's stop — clamp to the last chapter's start.
        return _segmentRanges[^1].startMs;
    }
}
